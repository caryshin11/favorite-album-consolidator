using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Favorite_Album_Consolidator.Models;
using Favorite_Album_Consolidator.Services;
using WMPLib;

namespace Favorite_Album_Consolidator
{
    /// <summary>
    /// iTunes preview playback + caching + hover-only inset controls with fade.
    /// Fix: only the active tile shows Pause while playing; other tiles show Play.
    /// </summary>
    public sealed class AlbumPreviewOverlayPlayer : IDisposable
    {
        private readonly ItunesPreviewService _previewService;
        private readonly WindowsMediaPlayer _player = new WindowsMediaPlayer();
        private readonly Random _rng = new Random();
        private readonly ToolTip _toolTip = new ToolTip();

        private readonly Dictionary<string, List<string>> _albumPreviewCache = new();

        // History for Previous behavior
        private readonly Stack<string> _history = new();
        private const double RestartThresholdSeconds = 2.0;

        private string? _currentPreviewUrl;
        private string? _currentAlbumKey;

        // Active tile + play state 
        private bool _isPlaying = false;
        private PictureBox? _activeCoverPb;

        // Keep references so redraw play icons for ALL tiles properly
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

            // Update play-state and refresh ALL tiles when playback changes
            _player.PlayStateChange += (newState) =>
            {
                var state = (WMPPlayState)newState;
                _isPlaying = state == WMPPlayState.wmppsPlaying;

                UpdateAllPlayIconsSafe();
            };
        }

        public void Dispose()
        {
            try { _player.controls.stop(); } catch { }
        }

        // ---------- Public API ----------

        /// <summary>
        /// Adds an inset top-row control bar that appears only on hover and fades in/out.
        /// Also forwards hover to setHover so GlowPanel stays lit while on the buttons.
        /// </summary>
        public void AttachOverlay(Control cellContainer, PictureBox coverPictureBox, Action<bool> setHover)
        {
            if (cellContainer == null) throw new ArgumentNullException(nameof(cellContainer));
            if (coverPictureBox == null) throw new ArgumentNullException(nameof(coverPictureBox));
            if (setHover == null) throw new ArgumentNullException(nameof(setHover));

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

            // Buttons
            var btnPrev = MakeOverlayButton("Previous", (s, e) =>
            {
                // Previous: restart if > threshold else go back in history
                Previous();
                UpdateAllPlayIconsSafe();
            });

            var btnPlay = MakeOverlayButton("Play / Pause", async (s, e) =>
            {
                if (coverPictureBox.Tag is not Album a) return;

                // Mark THIS tile active
                _activeCoverPb = coverPictureBox;

                // If nothing loaded or different album, start random; else toggle
                if (string.IsNullOrWhiteSpace(_currentPreviewUrl) || _currentAlbumKey != AlbumKey(a))
                    await PlayRandomAsync(a);
                else
                    TogglePlayPause();

                UpdateAllPlayIconsSafe();
            });

            var btnNext = MakeOverlayButton("Skip (Random)", async (s, e) =>
            {
                if (coverPictureBox.Tag is not Album a) return;

                // Mark THIS tile active
                _activeCoverPb = coverPictureBox;

                await PlayRandomAsync(a);

                UpdateAllPlayIconsSafe();
            });

            bar.Controls.Add(btnPrev, 0, 0);
            bar.Controls.Add(btnPlay, 1, 0);
            bar.Controls.Add(btnNext, 2, 0);

            cellContainer.Controls.Add(bar);
            bar.BringToFront();

            // --- Fade + sizing ---
            const int inset = 8;             // keeps glow border visible
            const int maxBgAlpha = 90;       // max button background alpha
            const int maxIconAlpha = 255;    // max icon alpha
            const int tickMs = 15;           // animation tick
            const int step = 18;             // alpha step per tick

            int labelHeight = GuessBottomLabelHeight(cellContainer);

            int bgAlpha = 0;
            int iconAlpha = 0;
            int targetBgAlpha = 0;
            int targetIconAlpha = 0;

            // current icon sizing values (per tile) for global icon refresh
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

                // Save per-tile values so UpdateAllPlayIconsSafe can redraw correctly
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

            // --- Robust hover detection (prevents lingering when MouseLeave is missed) 
            bool lastInside = false;

            bool IsCursorInCell()
            {
                // If handle isn't created or we're disposed, treat as not-hovering
                if (!cellContainer.IsHandleCreated) return false;

                Point p = cellContainer.PointToClient(Control.MousePosition);
                return cellContainer.ClientRectangle.Contains(p);
            }

            var hoverPoll = new System.Windows.Forms.Timer { Interval = 40 }; // ~25fps
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

            // Clean up timer when the cell goes away
            cellContainer.Disposed += (s, e) =>
            {
                try { hoverPoll.Stop(); } catch { }
                try { hoverPoll.Dispose(); } catch { }
            };

            // Also stop the fade timer when cell is disposed 
            cellContainer.Disposed += (s, e) =>
            {
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

            // Double-click cover = random track (and mark this tile active)
            coverPictureBox.DoubleClick += async (s, e) =>
            {
                if (coverPictureBox.Tag is not Album a) return;

                _activeCoverPb = coverPictureBox;

                await PlayRandomAsync(a);

                UpdateAllPlayIconsSafe();
            };
        }

        private void UpdateAllPlayIconsSafe()
        {
            // Remove disposed entries occasionally
            _playButtons.RemoveAll(x => x.PlayBtn.IsDisposed || x.CoverPb.IsDisposed);

            if (_playButtons.Count == 0) return;

            // Marshal to UI thread using any live button
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

        // ---------- Playback ----------

        private static string AlbumKey(Album a)
            => $"{(a.Artist ?? "").Trim().ToLowerInvariant()}|{(a.Title ?? "").Trim().ToLowerInvariant()}";

        private async Task<List<string>> GetCachedPreviewsAsync(Album album)
        {
            string key = AlbumKey(album);

            if (_albumPreviewCache.TryGetValue(key, out var cached) && cached.Count > 0)
                return cached;

            var urls = await _previewService.GetAlbumPreviewUrlsAsync(album, limit: 25);
            _albumPreviewCache[key] = urls;
            return urls;
        }

        private async Task PlayRandomAsync(Album album)
        {
            var urls = await GetCachedPreviewsAsync(album);
            if (urls.Count == 0)
            {
                MessageBox.Show("No iTunes previews found for this album.", "Preview",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // choose random (try to avoid immediate repeat)
            string chosen = urls[_rng.Next(urls.Count)];
            if (urls.Count > 1 && chosen == _currentPreviewUrl)
            {
                for (int i = 0; i < 4; i++)
                {
                    var candidate = urls[_rng.Next(urls.Count)];
                    if (candidate != _currentPreviewUrl) { chosen = candidate; break; }
                }
            }

            PlayUrl(chosen, AlbumKey(album));
        }

        private void PlayUrl(string url, string albumKey)
        {
            // push current into history (only if different)
            if (!string.IsNullOrWhiteSpace(_currentPreviewUrl) &&
                !string.Equals(_currentPreviewUrl, url, StringComparison.OrdinalIgnoreCase))
            {
                _history.Push(_currentPreviewUrl);
            }

            _currentPreviewUrl = url;
            _currentAlbumKey = albumKey;

            _player.controls.stop();
            _player.URL = url;
            _player.controls.play();
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
            // If currently playing and position past threshold, restart current preview
            try
            {
                if (!string.IsNullOrWhiteSpace(_currentPreviewUrl))
                {
                    double pos = _player.controls.currentPosition;
                    if (pos >= RestartThresholdSeconds)
                    {
                        _player.controls.currentPosition = 0;
                        return;
                    }
                }
            }
            catch
            {
                // ignore position errors
            }

            // otherwise go to previous preview in history
            if (_history.Count == 0) return;

            var url = _history.Pop();
            if (string.IsNullOrWhiteSpace(url)) return;

            _player.controls.stop();
            _player.URL = url;
            _player.controls.play();
            _currentPreviewUrl = url;
        }

        // ---------- UI: buttons + icons ----------

        private enum MiniIcon { Prev, Play, Pause, Next }

        private Button MakeOverlayButton(string tooltip, EventHandler onClick)
        {
            var btn = new Button
            {
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 0, 0, 0), // starts transparent
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
    }
}
