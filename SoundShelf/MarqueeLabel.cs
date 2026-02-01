using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace SoundShelf
{
    public class MarqueeLabel : Control
    {
        private readonly Timer _timer = new Timer();
        private int _offsetX = 0;
        private int _textWidth = 0;

        // Tweakable
        [System.ComponentModel.DefaultValue(2)]
        public int SpeedPxPerTick { get; set; } = 2;   // how fast it moves

        [System.ComponentModel.DefaultValue(30)]
        public int TickMs { get; set; } = 30;          // how smooth it is

        [System.ComponentModel.DefaultValue(30)]
        public int GapPx { get; set; } = 30;           // space between repeats

        [System.ComponentModel.DefaultValue(700)]
        public int StartDelayMs { get; set; } = 700;   // pause before scrolling starts

        // Rounded corners
        [System.ComponentModel.DefaultValue(10)]
        public int CornerRadius { get; set; } = 10;

        private int _delayLeftMs = 0;

        public MarqueeLabel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);

            _timer.Interval = TickMs;
            _timer.Tick += (s, e) => Step();
            _delayLeftMs = StartDelayMs;
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Recalculate();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            Recalculate();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateRegion();
            Recalculate();
        }

        private void Recalculate()
        {
            using var g = CreateGraphics();
            _textWidth = TextRenderer.MeasureText(g, Text ?? "", Font).Width;

            _offsetX = 0;
            _delayLeftMs = StartDelayMs;

            // Only animate if text is longer than available width
            if (_textWidth > ClientSize.Width && !string.IsNullOrWhiteSpace(Text))
                _timer.Start();
            else
                _timer.Stop();

            Invalidate();
        }

        private void Step()
        {
            if (_textWidth <= ClientSize.Width || string.IsNullOrWhiteSpace(Text))
                return;

            // delay at start (and after wrap)
            if (_delayLeftMs > 0)
            {
                _delayLeftMs -= _timer.Interval;
                return;
            }

            _offsetX -= SpeedPxPerTick;

            // when fully gone + gap, reset
            if (_offsetX < -(_textWidth + GapPx))
            {
                _offsetX = 0;
                _delayLeftMs = StartDelayMs;
            }

            Invalidate();
        }

        // Prevent default square background paint
        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            // do nothing
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (r.Width <= 0 || r.Height <= 0) return;

            using var path = GetRoundedPath(r, CornerRadius);

            // Fill rounded background
            using (var bg = new SolidBrush(BackColor))
                e.Graphics.FillPath(bg, path);

            if (string.IsNullOrWhiteSpace(Text))
                return;

            // Clip drawing to rounded rect so text doesn't bleed outside corners
            var oldClip = e.Graphics.Clip;
            e.Graphics.SetClip(path);

            // If it fits, draw centered
            if (_textWidth <= ClientSize.Width)
            {
                TextRenderer.DrawText(
                    e.Graphics,
                    Text,
                    Font,
                    ClientRectangle,
                    ForeColor,
                    TextFormatFlags.HorizontalCenter |
                    TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis
                );
            }
            else
            {
                // If it doesn't fit, draw scrolling text repeated
                var y = (ClientSize.Height - Font.Height) / 2;
                int x1 = _offsetX;
                int x2 = x1 + _textWidth + GapPx;

                TextRenderer.DrawText(e.Graphics, Text, Font, new Point(x1, y), ForeColor, BackColor);
                TextRenderer.DrawText(e.Graphics, Text, Font, new Point(x2, y), ForeColor, BackColor);
            }

            // Restore clip
            e.Graphics.Clip = oldClip;
        }

        private void UpdateRegion()
        {
            if (!IsHandleCreated || Width < 2 || Height < 2) return;

            using var path = GetRoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius);
            Region?.Dispose();
            Region = new Region(path);
        }

        private static GraphicsPath GetRoundedPath(Rectangle r, int radius)
        {
            var path = new GraphicsPath();

            if (radius <= 0)
            {
                path.AddRectangle(r);
                path.CloseFigure();
                return path;
            }

            int d = radius * 2;
            d = Math.Min(d, Math.Min(r.Width, r.Height));

            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();

            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
