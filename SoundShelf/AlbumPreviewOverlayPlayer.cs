using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using SoundShelf.Models;
using SoundShelf.Services;
using WMPLib;

namespace SoundShelf
{
    /// <summary>
    /// iTunes preview playback + caching + hover-only inset controls with fade.
    /// - Only active tile shows Pause while playing.
    /// - Label shows "Artist - Song" while previewing.
    /// - Label restores to "AlbumArtist - AlbumTitle" when playback stops/ends or when switching active tile.
    /// - Skip is random; Previous restarts current if > threshold else plays previous previewed track.
    /// </summary>
    public sealed class AlbumPreviewOverlayPlayer : IDisposable
    {
        private readonly ItunesPreviewService _previewService;
        private readonly WindowsMediaPlayer _player = new WindowsMediaPlayer();
        private readonly Random _rng = new Random();
        private readonly ToolTip _toolTip = new ToolTip();

        private readonly Dictionary<string, List<PreviewTrack>> _albumPreviewCache = new();

        private const double RestartThresholdSeconds = 2.0;

        private string? _currentAlbumKey;
        private PreviewTrack? _currentTrack;
        private const string NowPlayingPrefix = "NOW PLAYING · ";

        // Active tile tracking
        private bool _isPlaying = false;
        private PictureBox? _activeCoverPb;

        // Labels per tile so we can update/restore correctly
        private readonly Dictionary<PictureBox, MarqueeLabel> _tileLabelMap = new();

        // Remember last active tile label so we can restore on stop
        private MarqueeLabel? _activeTileLabel;

        // History includes which tile it came from (so "previous" can jump back across tiles)
        private sealed class HistoryEntry
        {
            public required PreviewTrack Track { get; init; }
            public required string AlbumKey { get; init; }
            public required PictureBox TilePb { get; init; }
        }

        private readonly Stack<HistoryEntry> _history = new();

        // Keep references so we can redraw play icons for ALL tiles properly
        private readonly List<PlayBtnReg> _playButtons = new();

        private sealed class PlayBtnReg
        {
            public required PictureBox CoverPb { get; init; }
            public required Button PlayBtn { get; init; }
            public required Func<int> GetIconSize { get; init; }
            public required Func<int> GetIconAlpha { get; init; }
        }

        public int Volume
        {
            get => _player.settings.volume;
            set => _player.settings.volume = Math.Max(0, Math.Min(100, value));
        }

        public AlbumPreviewOverlayPlayer(ItunesPreviewService previewService, int volume = 80)
        {
            _previewService = previewService ?? throw new ArgumentNullException(nameof(previewService));

            _player.settings.autoStart = false;
            _player.settings.mute = false;
            Volume = volume;

            _player.PlayStateChange += (newState) =>
            {
                var state = (WMPPlayState)newState;
                _isPlaying = state == WMPPlayState.wmppsPlaying;

                RunOnUiThread(() =>
                {
                    // When playback stops/ends: restore album caption
                    if (state == WMPPlayState.wmppsStopped || state == WMPPlayState.wmppsMediaEnded)
                    {
                        RestoreActiveTileLabelToAlbum();
                    }

                    // When playback starts again (after stop/pause): re-show song caption
                    if (state == WMPPlayState.wmppsPlaying && _currentTrack != null)
                    {
                        // Make sure we still have the correct active label pointer
                        if (_activeCoverPb != null && _tileLabelMap.TryGetValue(_activeCoverPb, out var lbl))
                            _activeTileLabel = lbl;

                        UpdateActiveTileLabelToSong(_currentTrack);
                    }

                    UpdateAllPlayIconsSafe();
                });
            };
        }

        public void Dispose()
        {
            try { _player.controls.stop(); } catch { }
        }

        // ---------- Public API ----------

        public void AttachOverlay(Control cellContainer, PictureBox coverPictureBox, Action<bool> setHover)
        {
            if (cellContainer == null) throw new ArgumentNullException(nameof(cellContainer));
            if (coverPictureBox == null) throw new ArgumentNullException(nameof(coverPictureBox));
            if (setHover == null) throw new ArgumentNullException(nameof(setHover));

            // Grab the tile label once and register it
            var tileLabel = cellContainer.Controls.OfType<MarqueeLabel>().FirstOrDefault();
            if (tileLabel != null)
                _tileLabelMap[coverPictureBox] = tileLabel;

            // --- Bar ---
            var bar = new TableLayoutPanel
            {
                ColumnCount = 3,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };

            bar.ColumnStyles.Clear();
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
            bar.RowStyles.Clear();
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

            // --- Fade + sizing ---
            const int inset = 8;
            const int maxBgAlpha = 90;
            const int maxIconAlpha = 255;
            const int tickMs = 15;
            const int step = 18;

            int labelHeight = GuessBottomLabelHeight(cellContainer);

            int bgAlpha = 0;
            int iconAlpha = 0;
            int targetBgAlpha = 0;
            int targetIconAlpha = 0;

            int currentIconSize = 18;
            int currentIconAlpha = 0;

            bar.Visible = false;

            var fadeTimer = new System.Windows.Forms.Timer { Interval = tickMs };

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

                if (bgAlpha != targetBgAlpha)
                {
                    bgAlpha = MoveToward(bgAlpha, targetBgAlpha, step);
                    done = false;
                }

                if (iconAlpha != targetIconAlpha)
                {
                    iconAlpha = MoveToward(iconAlpha, targetIconAlpha, step * 2);
                    done = false;
                }

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

            // Robust hover polling (prevents lingering when MouseLeave is missed)
            bool lastInside = false;

            bool IsCursorInCell()
            {
                if (!cellContainer.IsHandleCreated) return false;
                Point p = cellContainer.PointToClient(Control.MousePosition);
                return cellContainer.ClientRectangle.Contains(p);
            }

            var hoverPoll = new System.Windows.Forms.Timer { Interval = 40 };
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

                if (inside)
                {
                    setHover(true);
                    StartFadeIn();
                }
                else
                {
                    setHover(false);
                    StartFadeOut();
                }
            };
            hoverPoll.Start();

            cellContainer.Disposed += (s, e) =>
            {
                try { hoverPoll.Stop(); } catch { }
                try { hoverPoll.Dispose(); } catch { }
                try { fadeTimer.Stop(); } catch { }
                try { fadeTimer.Dispose(); } catch { }
            };

            ApplyAlpha();
            RefreshBarSizing();
            cellContainer.Resize += (s, e) => RefreshBarSizing();

            // Register this tile's play button for global icon refresh
            _playButtons.Add(new PlayBtnReg
            {
                CoverPb = coverPictureBox,
                PlayBtn = btnPlay,
                GetIconSize = () => currentIconSize,
                GetIconAlpha = () => currentIconAlpha
            });

            // Double-click cover = random track
            coverPictureBox.DoubleClick += async (s, e) =>
            {
                if (coverPictureBox.Tag is not Album a) return;

                SetActiveTile(coverPictureBox);

                await PlayRandomAsync(a, coverPictureBox);

                UpdateAllPlayIconsSafe();
            };
        }

        // ---------- Active tile + label management ----------

        private void SetActiveTile(PictureBox newPb)
        {
            // If switching tiles, restore the previous active tile label back to album caption
            if (_activeCoverPb != null && !ReferenceEquals(_activeCoverPb, newPb))
                RestoreTileLabelToAlbum(_activeCoverPb);

            _activeCoverPb = newPb;

            // Cache active label pointer (if exists)
            _activeTileLabel = _tileLabelMap.TryGetValue(newPb, out var lbl) ? lbl : null;
        }

        private void UpdateActiveTileLabelToSong(PreviewTrack track)
        {
            if (_activeTileLabel == null || _activeTileLabel.IsDisposed) return;

            _activeTileLabel.Text =
                $"{NowPlayingPrefix}{track.ArtistName} - {track.TrackName}";
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

            if (pb.Tag is Album a)
                lbl.Text = $"{a.Artist} - {a.Title}";
            else
                lbl.Text = "";
        }


        // ---------- Playback ----------

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

            var chosen = tracks[_rng.Next(tracks.Count)];

            // Avoid immediate repeat if possible
            if (_currentTrack != null && tracks.Count > 1 && chosen.PreviewUrl == _currentTrack.PreviewUrl)
            {
                for (int i = 0; i < 4; i++)
                {
                    var cand = tracks[_rng.Next(tracks.Count)];
                    if (cand.PreviewUrl != _currentTrack.PreviewUrl) { chosen = cand; break; }
                }
            }

            PlayTrack(chosen, AlbumKey(album), tilePb);
        }

        private void PlayTrack(PreviewTrack track, string albumKey, PictureBox tilePb)
        {
            // push current into history (if different)
            if (_currentTrack != null && !string.Equals(_currentTrack.PreviewUrl, track.PreviewUrl, StringComparison.OrdinalIgnoreCase)
                && _activeCoverPb != null && _currentAlbumKey != null)
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

            // Ensure tile is active for label + icon updates
            SetActiveTile(tilePb);

            _player.controls.stop();
            _player.URL = track.PreviewUrl;
            _player.controls.play();

            UpdateActiveTileLabelToSong(track);
        }

        private void TogglePlayPause()
        {
            if (_player.playState == WMPPlayState.wmppsPlaying)
                _player.controls.pause();
            else
                _player.controls.play();
        }

        private void Previous()
        {
            // Restart current preview if played enough
            try
            {
                if (_currentTrack != null)
                {
                    double pos = _player.controls.currentPosition;
                    if (pos >= RestartThresholdSeconds)
                    {
                        _player.controls.currentPosition = 0;
                        return;
                    }
                }
            }
            catch { /* ignore */ }

            // Otherwise go back in history (could be a different tile/album)
            if (_history.Count == 0) return;

            var entry = _history.Pop();
            if (entry.Track == null || string.IsNullOrWhiteSpace(entry.Track.PreviewUrl)) return;

            // Activate that tile and play that track
            SetActiveTile(entry.TilePb);

            _currentTrack = entry.Track;
            _currentAlbumKey = entry.AlbumKey;

            _player.controls.stop();
            _player.URL = entry.Track.PreviewUrl;
            _player.controls.play();

            UpdateActiveTileLabelToSong(entry.Track);
        }

        // ---------- Icons + UI helpers ----------

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
                    {
                        PointF p1 = new(r.Left + 1, r.Top);
                        PointF p2 = new(r.Right, r.Top + r.Height / 2f);
                        PointF p3 = new(r.Left + 1, r.Bottom);
                        g.FillPolygon(brush, new[] { p1, p2, p3 });
                        break;
                    }

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
                        PointF n1 = new(r.Left, r.Top);
                        PointF n2 = new(r.Right - barW - 1, r.Top + r.Height / 2f);
                        PointF n3 = new(r.Left, r.Bottom);
                        g.FillPolygon(brush, new[] { n1, n2, n3 });
                        g.FillRectangle(brush, r.Right - barW, r.Top, barW, r.Height);
                        break;
                    }

                case MiniIcon.Prev:
                    {
                        int barW2 = Math.Max(3, size / 8);
                        g.FillRectangle(brush, r.Left, r.Top, barW2, r.Height);

                        PointF v1 = new(r.Right, r.Top);
                        PointF v2 = new(r.Left + Math.Max(6, size / 3), r.Top + r.Height / 2f);
                        PointF v3 = new(r.Right, r.Bottom);
                        g.FillPolygon(brush, new[] { v1, v2, v3 });
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
            var bottom = cellContainer.Controls
                .OfType<Control>()
                .FirstOrDefault(c => c.Dock == DockStyle.Bottom);

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
    }
}
