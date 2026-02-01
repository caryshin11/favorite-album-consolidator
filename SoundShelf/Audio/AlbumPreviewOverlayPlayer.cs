using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using SoundShelf.Models;
using SoundShelf.Services;

namespace SoundShelf.Audio
{
    using WinFormsTimer = System.Windows.Forms.Timer;

    public sealed class AlbumPreviewOverlayPlayer : IDisposable
    {
        private readonly ItunesPreviewService _previewService;
        private readonly PreviewAudioEngine _engine;
        private readonly Random _rng = new Random();
        private readonly ToolTip _toolTip = new ToolTip();

        private readonly Dictionary<string, List<PreviewTrack>> _albumPreviewCache = new();

        // Avoid repeats (per album)
        private const int RecentPerAlbum = 8;
        private readonly Dictionary<string, Queue<string>> _recentByAlbum = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _recentSetByAlbum = new(StringComparer.OrdinalIgnoreCase);

        // Progress bar per tile
        private readonly Dictionary<PictureBox, PreviewProgressBar> _tileProgress = new();
        private readonly WinFormsTimer _progressPoll = new WinFormsTimer { Interval = 50 }; // smooth

        private const string NowPlayingPrefix = "NOW PLAYING · ";

        // Playback state
        private bool _isPlaying = false;

        // Track state
        private string? _currentAlbumKey;
        private PreviewTrack? _currentTrack;
        private List<PreviewTrack>? _currentTrackList;
        private int _currentTrackIndex = -1;

        // Active tile tracking
        private PictureBox? _activeCoverPb;

        // Labels per tile
        private readonly Dictionary<PictureBox, MarqueeLabel> _tileLabelMap = new();
        private MarqueeLabel? _activeTileLabel;

        // History
        private sealed class HistoryEntry
        {
            public required PreviewTrack Track { get; init; }
            public required string AlbumKey { get; init; }
            public required PictureBox TilePb { get; init; }
        }
        private readonly Stack<HistoryEntry> _history = new();

        // Play icon registry
        private readonly List<PlayBtnReg> _playButtons = new();
        private sealed class PlayBtnReg
        {
            public required PictureBox CoverPb { get; init; }
            public required Button PlayBtn { get; init; }
            public required Func<int> GetIconSize { get; init; }
            public required Func<int> GetIconAlpha { get; init; }
        }

        // Shuffle cycle per album
        private sealed class ShuffleState
        {
            public List<string> OrderUrls = new();
            public int Pos = 0;
            public int TrackCountSnapshot = 0;

            public void Reset(List<PreviewTrack> tracks, Random rng)
            {
                OrderUrls = tracks
                    .Where(t => !string.IsNullOrWhiteSpace(t.PreviewUrl))
                    .Select(t => t.PreviewUrl)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                for (int i = OrderUrls.Count - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    (OrderUrls[i], OrderUrls[j]) = (OrderUrls[j], OrderUrls[i]);
                }

                Pos = 0;
                TrackCountSnapshot = tracks.Count;
            }

            public bool Exhausted => Pos >= OrderUrls.Count;
        }
        private readonly Dictionary<string, ShuffleState> _shuffleByAlbum = new();

        // Volume passthrough
        private int _baseVolume = 80;
        public int Volume
        {
            get => _baseVolume;
            set
            {
                _baseVolume = Math.Max(0, Math.Min(100, value));
                _engine.SetVolume(_baseVolume);
            }
        }

        // State poll (updates label/icons when paused/ended)
        private readonly WinFormsTimer _statePoll = new WinFormsTimer { Interval = 200 };

        // Crossfade
        public int CrossfadeMs { get; set; } = 700;
        private readonly WinFormsTimer _crossfadePoll = new WinFormsTimer { Interval = 120 };
        private bool _crossfadeArmed = false;

        public AlbumPreviewOverlayPlayer(ItunesPreviewService previewService, PreviewAudioEngine engine, int volume = 80)
        {
            _previewService = previewService ?? throw new ArgumentNullException(nameof(previewService));
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));

            Volume = volume;

            _statePoll.Tick += (s, e) => PollPlaybackState();
            _statePoll.Start();

            _crossfadePoll.Tick += (s, e) => CrossfadePollTick();
            _crossfadePoll.Start();

            _progressPoll.Tick += (s, e) => ProgressPollTick();
            _progressPoll.Start();
        }

        public void Dispose()
        {
            try { _statePoll.Stop(); } catch { }
            try { _statePoll.Dispose(); } catch { }

            try { _crossfadePoll.Stop(); } catch { }
            try { _crossfadePoll.Dispose(); } catch { }

            try { _progressPoll.Stop(); } catch { }
            try { _progressPoll.Dispose(); } catch { }

            // Engine owned by Form1; stopping is fine
            try { _engine.Stop(); } catch { }
        }

        public void AttachOverlay(Control cellContainer, PictureBox coverPictureBox, Action<bool> setHover)
        {
            if (cellContainer == null) throw new ArgumentNullException(nameof(cellContainer));
            if (coverPictureBox == null) throw new ArgumentNullException(nameof(coverPictureBox));
            if (setHover == null) throw new ArgumentNullException(nameof(setHover));

            // register tile label
            var tileLabel = cellContainer.Controls.OfType<MarqueeLabel>().FirstOrDefault();
            if (tileLabel != null)
                _tileLabelMap[coverPictureBox] = tileLabel;

            // ---- Progress bar ON TOP OF COVER ART ----
            var prog = new PreviewProgressBar
            {
                Dock = DockStyle.Bottom,
                Height = 4,
                Visible = false,
                Margin = Padding.Empty
            };

            // IMPORTANT: add to the PictureBox, not the cell container
            coverPictureBox.Controls.Add(prog);
            prog.BringToFront();

            _tileProgress[coverPictureBox] = prog;

            // overlay bar
            var bar = new TableLayoutPanel
            {
                ColumnCount = 3,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
            bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            var btnPrev = MakeOverlayButton("Previous", (s, e) =>
            {
                Previous();
                UpdateAllPlayIconsSafe();
            });

            var btnPlay = MakeOverlayButton("Play / Pause", async (s, e) =>
            {
                if (coverPictureBox.Tag is not Album a) return;
                SetActiveTile(coverPictureBox);

                if (_currentTrack == null || _currentAlbumKey != AlbumKey(a))
                    await PlayRandomAsync(a, coverPictureBox);
                else
                    TogglePlayPause();

                UpdateAllPlayIconsSafe();
            });

            var btnNext = MakeOverlayButton("Skip (Random)", async (s, e) =>
            {
                if (coverPictureBox.Tag is not Album a) return;
                SetActiveTile(coverPictureBox);
                await PlayRandomAsync(a, coverPictureBox);
                UpdateAllPlayIconsSafe();
            });

            bar.Controls.Add(btnPrev, 0, 0);
            bar.Controls.Add(btnPlay, 1, 0);
            bar.Controls.Add(btnNext, 2, 0);

            cellContainer.Controls.Add(bar);
            bar.BringToFront();

            // fade + sizing
            const int inset = 8;
            const int maxBgAlpha = 90;
            const int maxIconAlpha = 255;
            const int tickMs = 15;
            const int step = 18;

            int labelHeight = GuessBottomLabelHeight(cellContainer);
            int bgAlpha = 0, iconAlpha = 0, targetBgAlpha = 0, targetIconAlpha = 0;
            int currentIconSize = 18, currentIconAlpha = 0;

            bar.Visible = false;
            var fadeTimer = new WinFormsTimer { Interval = tickMs };

            void ApplyAlpha()
            {
                var bg = Color.FromArgb(bgAlpha, 0, 0, 0);
                btnPrev.BackColor = bg;
                btnPlay.BackColor = bg;
                btnNext.BackColor = bg;
            }

            void RefreshBarSizing()
            {
                int cellW = cellContainer.ClientSize.Width;
                int cellH = cellContainer.ClientSize.Height;
                int coverH = Math.Max(0, cellH - labelHeight);

                int barH = (int)Math.Round(Math.Max(26, Math.Min(56, coverH * 0.18)));
                int barW = Math.Max(0, cellW - inset * 2);

                bar.Location = new Point(inset, inset);
                bar.Size = new Size(barW, barH);

                int iconSize = Math.Max(12, barH - 12);

                currentIconSize = iconSize;
                currentIconAlpha = iconAlpha;

                btnPrev.Image = MakeIcon(MiniIcon.Prev, iconSize, iconAlpha);
                btnNext.Image = MakeIcon(MiniIcon.Next, iconSize, iconAlpha);

                bool isThisTileActive = ReferenceEquals(coverPictureBox, _activeCoverPb);
                bool showPause = _isPlaying && isThisTileActive;
                btnPlay.Image = MakeIcon(showPause ? MiniIcon.Pause : MiniIcon.Play, iconSize, iconAlpha);
            }

            fadeTimer.Tick += (s, e) =>
            {
                bool done = true;

                if (bgAlpha != targetBgAlpha) { bgAlpha = MoveToward(bgAlpha, targetBgAlpha, step); done = false; }
                if (iconAlpha != targetIconAlpha) { iconAlpha = MoveToward(iconAlpha, targetIconAlpha, step * 2); done = false; }

                ApplyAlpha();
                RefreshBarSizing();

                if (done)
                {
                    fadeTimer.Stop();
                    if (targetBgAlpha == 0 && targetIconAlpha == 0)
                        bar.Visible = false;
                }
            };

            void StartFadeIn()
            {
                if (!bar.Visible) bar.Visible = true;
                targetBgAlpha = maxBgAlpha;
                targetIconAlpha = maxIconAlpha;
                if (!fadeTimer.Enabled) fadeTimer.Start();
            }

            void StartFadeOut()
            {
                targetBgAlpha = 0;
                targetIconAlpha = 0;
                if (!fadeTimer.Enabled) fadeTimer.Start();
            }

            // hover polling
            bool lastInside = false;
            bool IsCursorInCell()
            {
                if (!cellContainer.IsHandleCreated) return false;
                Point p = cellContainer.PointToClient(Control.MousePosition);
                return cellContainer.ClientRectangle.Contains(p);
            }

            var hoverPoll = new WinFormsTimer { Interval = 40 };
            hoverPoll.Tick += (s, e) =>
            {
                if (cellContainer.IsDisposed)
                {
                    hoverPoll.Stop();
                    hoverPoll.Dispose();
                    return;
                }

                bool inside = IsCursorInCell();
                if (inside == lastInside) return;
                lastInside = inside;

                if (inside) { setHover(true); StartFadeIn(); }
                else { setHover(false); StartFadeOut(); }
            };
            hoverPoll.Start();

            cellContainer.Disposed += (s, e) =>
            {
                try { hoverPoll.Stop(); } catch { }
                try { hoverPoll.Dispose(); } catch { }
                try { fadeTimer.Stop(); } catch { }
                try { fadeTimer.Dispose(); } catch { }

                // cleanup progress bar registry
                _tileProgress.Remove(coverPictureBox);
            };

            ApplyAlpha();
            RefreshBarSizing();
            cellContainer.Resize += (s, e) => RefreshBarSizing();

            _playButtons.Add(new PlayBtnReg
            {
                CoverPb = coverPictureBox,
                PlayBtn = btnPlay,
                GetIconSize = () => currentIconSize,
                GetIconAlpha = () => currentIconAlpha
            });

            coverPictureBox.DoubleClick += async (s, e) =>
            {
                if (coverPictureBox.Tag is not Album a) return;
                SetActiveTile(coverPictureBox);
                await PlayRandomAsync(a, coverPictureBox);
                UpdateAllPlayIconsSafe();
            };
        }

        // ---------------- Progress Poll ----------------

        private void ProgressPollTick()
        {
            // Remove disposed entries
            var dead = _tileProgress.Where(kv => kv.Key.IsDisposed || kv.Value.IsDisposed).Select(kv => kv.Key).ToList();
            foreach (var k in dead) _tileProgress.Remove(k);

            if (_activeCoverPb == null) { HideAllProgress(); return; }
            if (!_tileProgress.TryGetValue(_activeCoverPb, out var bar) || bar.IsDisposed) return;

            // Only show while actively playing
            if (!_isPlaying || _engine.PlaybackState != NAudio.Wave.PlaybackState.Playing)
            {
                bar.Visible = false;
                bar.Progress = 0f;
                return;
            }

            float? p = null;

            // Preferred: engine provides progress 0..1
            try
            {
                p = _engine.GetCurrentProgress01();
            }
            catch
            {
                try
                {
                    double? rem = _engine.GetCurrentRemainingSeconds();
                    double? dur = _engine.GetCurrentDurationSeconds();

                    if (rem.HasValue && dur.HasValue && dur.Value > 0)
                    {
                        p = (float)Math.Max(
                            0.0,
                            Math.Min(1.0, 1.0 - (rem.Value / dur.Value))
                        );
                    }
                }
                catch
                {
                    p = null;
                }
            }

            if (p == null)
            {
                bar.Visible = false;
                bar.Progress = 0f;
                return;
            }

            bar.Visible = true;
            bar.Progress = Math.Max(0f, Math.Min(1f, p.Value));

            // Hide other tiles’ progress bars
            foreach (var kv in _tileProgress)
            {
                if (!ReferenceEquals(kv.Key, _activeCoverPb) && kv.Value != null && !kv.Value.IsDisposed)
                {
                    kv.Value.Visible = false;
                    kv.Value.Progress = 0f;
                }
            }
        }

        private void HideAllProgress()
        {
            foreach (var kv in _tileProgress)
            {
                if (kv.Value == null || kv.Value.IsDisposed) continue;
                kv.Value.Visible = false;
                kv.Value.Progress = 0f;
            }
        }

        // ---------------- Tile label ----------------

        private void SetActiveTile(PictureBox newPb)
        {
            if (_activeCoverPb != null && !ReferenceEquals(_activeCoverPb, newPb))
                RestoreTileLabelToAlbum(_activeCoverPb);

            _activeCoverPb = newPb;
            _activeTileLabel = _tileLabelMap.TryGetValue(newPb, out var lbl) ? lbl : null;

            // When switching tiles, ensure only the new tile can show progress
            ProgressPollTick();
        }

        private void UpdateActiveTileLabelToSong(PreviewTrack track)
        {
            if (_activeTileLabel == null || _activeTileLabel.IsDisposed) return;
            _activeTileLabel.Text = $"{NowPlayingPrefix}{track.ArtistName} - {track.TrackName}";
        }

        private void RestoreActiveTileLabelToAlbum()
        {
            if (_activeCoverPb == null) return;
            RestoreTileLabelToAlbum(_activeCoverPb);
        }

        private void RestoreTileLabelToAlbum(PictureBox pb)
        {
            if (!_tileLabelMap.TryGetValue(pb, out var lbl)) return;
            if (lbl.IsDisposed) return;

            if (pb.Tag is Album a) lbl.Text = $"{a.Artist} - {a.Title}";
            else lbl.Text = "";
        }

        // ---------------- Playback ----------------

        private static string AlbumKey(Album a)
            => $"{(a.Artist ?? "").Trim().ToLowerInvariant()}|{(a.Title ?? "").Trim().ToLowerInvariant()}";

        private async Task<List<PreviewTrack>> GetCachedPreviewsAsync(Album album)
        {
            string key = AlbumKey(album);

            if (_albumPreviewCache.TryGetValue(key, out var cached) && cached.Count > 0)
                return cached;

            var tracks = await _previewService.GetAlbumPreviewsAsync(album, limit: 25);
            _albumPreviewCache[key] = tracks;
            return tracks;
        }

        private async Task PlayRandomAsync(Album album, PictureBox tilePb)
        {
            var tracks = await GetCachedPreviewsAsync(album);
            if (tracks.Count == 0)
            {
                MessageBox.Show("No iTunes previews found for this album.", "Preview",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _currentTrackList = tracks;

            string key = AlbumKey(album);
            var chosen = PickNextShuffleTrack(key, tracks);

            // avoid immediate repeat
            if (_currentTrack != null && tracks.Count > 1 && chosen.PreviewUrl == _currentTrack.PreviewUrl)
            {
                for (int i = 0; i < 4; i++)
                {
                    var cand = tracks[_rng.Next(tracks.Count)];
                    if (cand.PreviewUrl != _currentTrack.PreviewUrl) { chosen = cand; break; }
                }
            }

            _currentTrackIndex = tracks.FindIndex(t => t.PreviewUrl == chosen.PreviewUrl);
            PlayTrack(chosen, AlbumKey(album), tilePb);
        }

        private void RememberRecent(string albumKey, string previewUrl)
        {
            if (string.IsNullOrWhiteSpace(albumKey) || string.IsNullOrWhiteSpace(previewUrl))
                return;

            if (!_recentByAlbum.TryGetValue(albumKey, out var q))
                _recentByAlbum[albumKey] = q = new Queue<string>();

            if (!_recentSetByAlbum.TryGetValue(albumKey, out var set))
                _recentSetByAlbum[albumKey] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (set.Contains(previewUrl))
            {
                var tmp = q.Where(u => !string.Equals(u, previewUrl, StringComparison.OrdinalIgnoreCase)).ToList();
                q.Clear();
                set.Clear();
                foreach (var u in tmp) { q.Enqueue(u); set.Add(u); }
            }

            q.Enqueue(previewUrl);
            set.Add(previewUrl);

            while (q.Count > RecentPerAlbum)
                set.Remove(q.Dequeue());
        }

        private bool IsRecent(string albumKey, string previewUrl)
        {
            return _recentSetByAlbum.TryGetValue(albumKey, out var set) &&
                   set.Contains(previewUrl);
        }

        private PreviewTrack PickNextShuffleTrack(string albumKey, List<PreviewTrack> tracks)
        {
            if (!_shuffleByAlbum.TryGetValue(albumKey, out var st))
                _shuffleByAlbum[albumKey] = st = new ShuffleState();

            if (st.OrderUrls.Count == 0 || st.Exhausted || st.TrackCountSnapshot != tracks.Count)
                st.Reset(tracks, _rng);

            var map = tracks
                .Where(t => !string.IsNullOrWhiteSpace(t.PreviewUrl))
                .GroupBy(t => t.PreviewUrl, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            int total = map.Count;
            int recentLimit = Math.Min(RecentPerAlbum, Math.Max(1, total - 1));
            int guard = Math.Max(12, total * 2);

            while (guard-- > 0)
            {
                if (st.Exhausted)
                    st.Reset(tracks, _rng);

                string url = st.OrderUrls[st.Pos++];
                if (!map.TryGetValue(url, out var track))
                    continue;

                bool immediateRepeat =
                    _currentTrack != null &&
                    string.Equals(track.PreviewUrl, _currentTrack.PreviewUrl, StringComparison.OrdinalIgnoreCase);

                bool recent =
                    recentLimit > 0 &&
                    IsRecent(albumKey, track.PreviewUrl) &&
                    total > 1;

                if (immediateRepeat || recent)
                    continue;

                return track;
            }

            // Fallback (small albums)
            return map.Values.First();
        }

        private void PlayTrack(PreviewTrack track, string albumKey, PictureBox tilePb)
        {
            _engine.Stop();
            _crossfadeArmed = false;

            if (_currentTrack != null &&
                !string.Equals(_currentTrack.PreviewUrl, track.PreviewUrl, StringComparison.OrdinalIgnoreCase) &&
                _activeCoverPb != null && _currentAlbumKey != null)
            {
                _history.Push(new HistoryEntry
                {
                    Track = _currentTrack,
                    AlbumKey = _currentAlbumKey,
                    TilePb = _activeCoverPb
                });
            }

            _currentTrack = track;
            _currentAlbumKey = albumKey;

            // remember what actually played
            RememberRecent(albumKey, track.PreviewUrl);

            SetActiveTile(tilePb);

            _engine.SetVolume(_baseVolume);
            _engine.PlayUrl(track.PreviewUrl);

            _isPlaying = true;
            UpdateActiveTileLabelToSong(track);
            UpdateAllPlayIconsSafe();

            // ensure bar appears immediately
            ProgressPollTick();
        }

        private void TogglePlayPause()
        {
            if (_engine.PlaybackState == NAudio.Wave.PlaybackState.Playing)
            {
                _engine.Pause();
                _isPlaying = false;
            }
            else
            {
                _engine.Resume();
                _isPlaying = true;

                if (_currentTrack != null)
                    UpdateActiveTileLabelToSong(_currentTrack);
            }

            UpdateAllPlayIconsSafe();
            ProgressPollTick();
        }

        private void Previous()
        {
            if (_history.Count == 0)
            {
                if (_currentTrack != null && !string.IsNullOrWhiteSpace(_currentTrack.PreviewUrl) && _activeCoverPb != null)
                {
                    _engine.Stop();
                    _crossfadeArmed = false;

                    _engine.SetVolume(_baseVolume);
                    _engine.PlayUrl(_currentTrack.PreviewUrl);

                    _isPlaying = true;
                    UpdateActiveTileLabelToSong(_currentTrack);
                    UpdateAllPlayIconsSafe();

                    ProgressPollTick();
                }
                return;
            }

            var entry = _history.Pop();
            if (string.IsNullOrWhiteSpace(entry.Track.PreviewUrl)) return;

            _engine.Stop();
            _crossfadeArmed = false;

            SetActiveTile(entry.TilePb);

            _currentTrack = entry.Track;
            _currentAlbumKey = entry.AlbumKey;

            if (_activeCoverPb?.Tag is Album album)
            {
                var key = AlbumKey(album);
                if (_albumPreviewCache.TryGetValue(key, out var list) && list.Count > 0)
                {
                    _currentTrackList = list;
                    _currentTrackIndex = list.FindIndex(t => t.PreviewUrl == entry.Track.PreviewUrl);
                }
            }

            _engine.SetVolume(_baseVolume);
            _engine.PlayUrl(entry.Track.PreviewUrl);

            _isPlaying = true;
            UpdateActiveTileLabelToSong(entry.Track);
            UpdateAllPlayIconsSafe();

            ProgressPollTick();
        }

        private void PollPlaybackState()
        {
            bool nowPlaying = _engine.PlaybackState == NAudio.Wave.PlaybackState.Playing;
            if (nowPlaying == _isPlaying) return;

            _isPlaying = nowPlaying;

            RunOnUiThread(() =>
            {
                if (_isPlaying)
                {
                    if (_currentTrack != null)
                        UpdateActiveTileLabelToSong(_currentTrack);
                }
                else
                {
                    RestoreActiveTileLabelToAlbum();
                    HideAllProgress();
                }

                UpdateAllPlayIconsSafe();
            });
        }

        private void CrossfadePollTick()
        {
            if (!_isPlaying) return;
            if (_activeCoverPb == null) return;
            if (_activeCoverPb.Tag is not Album album) return;
            if (_currentTrackList == null || _currentTrackList.Count == 0) return;
            if (_engine.PlaybackState != NAudio.Wave.PlaybackState.Playing) return;

            var remaining = _engine.GetCurrentRemainingSeconds();
            if (remaining == null) return;

            double window = Math.Max(0.25, CrossfadeMs / 1000.0);

            if (remaining <= window)
            {
                if (_crossfadeArmed) return;
                _crossfadeArmed = true;

                // crossfade should follow the same shuffle logic as "Skip"
                string key = AlbumKey(album);

                var next = PickNextShuffleTrack(key, _currentTrackList);
                if (next == null || string.IsNullOrWhiteSpace(next.PreviewUrl)) return;

                _currentTrackIndex = _currentTrackList.FindIndex(t =>
                    string.Equals(t.PreviewUrl, next.PreviewUrl, StringComparison.OrdinalIgnoreCase));

                // Update UI to incoming track
                _currentTrack = next;
                _currentAlbumKey = key;
                UpdateActiveTileLabelToSong(next);
                UpdateAllPlayIconsSafe();

                // Remember it now so skip won't bounce back to the previous during fade window
                RememberRecent(key, next.PreviewUrl);

                _engine.CrossfadeTo(next.PreviewUrl, CrossfadeMs);

                // progress will keep updating as engine updates time
                ProgressPollTick();
            }
            else
            {
                _crossfadeArmed = false;
            }
        }

        // ---------------- UI helpers ----------------

        private enum MiniIcon { Prev, Play, Pause, Next }

        private Button MakeOverlayButton(string tooltip, EventHandler onClick)
        {
            var btn = new Button
            {
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 0, 0, 0),
                ForeColor = Color.White,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                TabStop = false,
                Cursor = Cursors.Hand,
                ImageAlign = ContentAlignment.MiddleCenter,
                Text = ""
            };

            btn.FlatAppearance.BorderSize = 0;
            btn.Click += onClick;

            _toolTip.SetToolTip(btn, tooltip);
            return btn;
        }

        private static Bitmap MakeIcon(MiniIcon which, int size, int alpha)
        {
            var bmp = new Bitmap(size, size);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            int a = Math.Max(0, Math.Min(255, alpha));
            using var brush = new SolidBrush(Color.FromArgb(a, 255, 255, 255));

            int pad = Math.Max(2, size / 6);
            var r = new Rectangle(pad, pad, size - pad * 2, size - pad * 2);

            switch (which)
            {
                case MiniIcon.Play:
                    g.FillPolygon(brush, new[]
                    {
                        new PointF(r.Left + 1, r.Top),
                        new PointF(r.Right, r.Top + r.Height / 2f),
                        new PointF(r.Left + 1, r.Bottom)
                    });
                    break;

                case MiniIcon.Pause:
                    {
                        int w = Math.Max(2, r.Width / 5);
                        int gap = Math.Max(2, r.Width / 6);
                        int x1 = r.Left + gap;
                        int x2 = r.Right - gap - w;
                        g.FillRectangle(brush, x1, r.Top, w, r.Height);
                        g.FillRectangle(brush, x2, r.Top, w, r.Height);
                        break;
                    }

                case MiniIcon.Next:
                    {
                        int barW = Math.Max(3, size / 8);
                        g.FillPolygon(brush, new[]
                        {
                            new PointF(r.Left, r.Top),
                            new PointF(r.Right - barW - 1, r.Top + r.Height / 2f),
                            new PointF(r.Left, r.Bottom)
                        });
                        g.FillRectangle(brush, r.Right - barW, r.Top, barW, r.Height);
                        break;
                    }

                case MiniIcon.Prev:
                    {
                        int barW2 = Math.Max(3, size / 8);
                        g.FillRectangle(brush, r.Left, r.Top, barW2, r.Height);
                        g.FillPolygon(brush, new[]
                        {
                            new PointF(r.Right, r.Top),
                            new PointF(r.Left + Math.Max(6, size / 3), r.Top + r.Height / 2f),
                            new PointF(r.Right, r.Bottom)
                        });
                        break;
                    }
            }

            return bmp;
        }

        private static int MoveToward(int current, int target, int delta)
        {
            if (current < target) return Math.Min(target, current + delta);
            if (current > target) return Math.Max(target, current - delta);
            return current;
        }

        private static int GuessBottomLabelHeight(Control cellContainer)
        {
            var bottom = cellContainer.Controls.OfType<Control>().FirstOrDefault(c => c.Dock == DockStyle.Bottom);
            return bottom?.Height > 0 ? bottom.Height : 30;
        }

        private void UpdateAllPlayIconsSafe()
        {
            _playButtons.RemoveAll(x => x.PlayBtn.IsDisposed || x.CoverPb.IsDisposed);
            if (_playButtons.Count == 0) return;

            var anyBtn = _playButtons[0].PlayBtn;
            if (anyBtn.IsDisposed) return;

            void Update()
            {
                foreach (var reg in _playButtons.ToList())
                {
                    if (reg.PlayBtn.IsDisposed) continue;

                    int size = reg.GetIconSize();
                    int alpha = reg.GetIconAlpha();

                    bool isActive = ReferenceEquals(reg.CoverPb, _activeCoverPb);
                    bool showPause = _isPlaying && isActive;

                    reg.PlayBtn.Image = MakeIcon(showPause ? MiniIcon.Pause : MiniIcon.Play, size, alpha);
                }
            }

            if (anyBtn.InvokeRequired) anyBtn.BeginInvoke((Action)Update);
            else Update();
        }

        private void RunOnUiThread(Action action)
        {
            if (_playButtons.Count > 0)
            {
                var anyBtn = _playButtons[0].PlayBtn;
                if (anyBtn != null && !anyBtn.IsDisposed && anyBtn.InvokeRequired)
                {
                    anyBtn.BeginInvoke(action);
                    return;
                }
            }
            action();
        }

        // ---------------- Progress Bar Control ----------------

        internal sealed class PreviewProgressBar : Control
        {
            private float _progress; // 0..1

            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public float Progress
            {
                get => _progress;
                set
                {
                    _progress = Math.Max(0f, Math.Min(1f, value));
                    Invalidate();
                }
            }

            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public Color TrackColor { get; set; } = Color.FromArgb(40, 255, 255, 255);

            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public Color FillColor { get; set; } = Color.FromArgb(200, 255, 255, 255);

            public PreviewProgressBar()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint |
                         ControlStyles.ResizeRedraw, true);

                Height = 4;
                TabStop = false;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(Parent?.BackColor ?? BackColor);

                var r = ClientRectangle;
                if (r.Width <= 0 || r.Height <= 0) return;

                using (var track = new SolidBrush(TrackColor))
                    e.Graphics.FillRectangle(track, r);

                int w = (int)Math.Round(r.Width * _progress);
                if (w > 0)
                {
                    using var fill = new SolidBrush(FillColor);
                    e.Graphics.FillRectangle(fill, new Rectangle(r.X, r.Y, w, r.Height));
                }
            }
        }
    }
}
