using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using SoundShelf.Audio; // EqualizerConfig
using System.ComponentModel;

namespace SoundShelf
{
    // Draws translucent equalizer bars behind the grid. Feed it 0..1 bar values.
    public sealed class EqualizerControl : Control
    {
        private float[] _target = Array.Empty<float>();
        private float[] _display = Array.Empty<float>();

        // Animation knobs (default from config; you can override in code if you want)
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public float Attack { get; set; } = EqualizerConfig.Attack; // higher = faster rise
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public float Decay { get; set; } = EqualizerConfig.Decay;   // lower = faster fall
        public int BarCount => _display.Length;

        private readonly System.Windows.Forms.Timer _repaintTimer =
            new System.Windows.Forms.Timer { Interval = EqualizerConfig.FrameMs };

        public EqualizerControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);

            // IMPORTANT: WinForms doesn't truly support transparent for all controls.
            // We paint a flat background that matches gridHost, then draw translucent bars on top.
            BackColor = EqualizerConfig.Background;

            // Purely visual; don't capture focus / input
            Enabled = false;
            TabStop = false;

            // Apply config defaults
            Attack = EqualizerConfig.Attack;
            Decay = EqualizerConfig.Decay;
            _repaintTimer.Interval = EqualizerConfig.FrameMs;

            _repaintTimer.Tick += (s, e) => Invalidate();
            _repaintTimer.Start();
        }

        /// <summary>
        /// Supply bar values in range 0..1. Length determines bar count.
        /// Call from UI thread; if calling from background, BeginInvoke first.
        /// </summary>
        public void SetBars(float[] bars)
        {
            if (bars == null || bars.Length == 0) return;

            if (_display.Length != bars.Length)
            {
                _display = new float[bars.Length];
                _target = new float[bars.Length];
            }

            for (int i = 0; i < bars.Length; i++)
                _target[i] = Clamp01(bars[i]);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Fill background (no Transparent to avoid ArgumentException)
            g.Clear(BackColor);

            if (_display.Length == 0) return;

            // Smooth values each frame (attack/decay)
            for (int i = 0; i < _display.Length; i++)
            {
                float cur = _display[i];
                float tgt = _target[i];

                if (tgt > cur)
                    cur = cur + (tgt - cur) * Attack;
                else
                    cur *= Decay;

                _display[i] = Clamp01(cur);
            }

            int w = ClientSize.Width;
            int h = ClientSize.Height;
            if (w <= 0 || h <= 0) return;

            int pad = EqualizerConfig.PaddingPx;
            int usableW = Math.Max(1, w - pad * 2);
            int usableH = Math.Max(1, h - pad * 2);

            int n = _display.Length;

            float gap = EqualizerConfig.GapPx;
            float barW = Math.Max(2f, (usableW - gap * (n - 1)) / n);

            using var body = new SolidBrush(EqualizerConfig.BarBody);
            using var cap = new SolidBrush(EqualizerConfig.BarCap);

            for (int i = 0; i < n; i++)
            {
                float v = _display[i];

                // Keep a small baseline so silence doesn't look dead
                float bh = Math.Max(EqualizerConfig.MinBarHeightPx, v * usableH);

                float x = pad + i * (barW + gap);
                float y = pad + (usableH - bh);

                var rect = new RectangleF(x, y, barW, bh);

                float r = Math.Min(EqualizerConfig.MaxCornerRadius, barW * 0.5f);

                using (var p = RoundedRect(rect, r))
                    g.FillPath(body, p);

                // Top cap highlight
                var capRect = new RectangleF(x, y, barW, Math.Min(10f, bh));
                using (var p2 = RoundedRect(capRect, r))
                    g.FillPath(cap, p2);
            }
        }

        private static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            if (rect.Width <= 0 || rect.Height <= 0) return path;

            float d = radius * 2f;
            if (d > rect.Width) d = rect.Width;
            if (d > rect.Height) d = rect.Height;

            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static float Clamp01(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        protected override void Dispose(bool disposing)
        {
            if (disposing) _repaintTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
