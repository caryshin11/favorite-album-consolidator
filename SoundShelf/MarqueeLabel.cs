using System;
using System.Collections.Generic;
using System.Text;

using System.Drawing;
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

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            e.Graphics.Clear(BackColor);

            if (string.IsNullOrWhiteSpace(Text))
                return;

            // If it fits, just draw centered
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
                return;
            }

            // If it doesn't fit, draw scrolling text repeated
            var y = (ClientSize.Height - Font.Height) / 2;
            int x1 = _offsetX;
            int x2 = x1 + _textWidth + GapPx;

            TextRenderer.DrawText(e.Graphics, Text, Font, new Point(x1, y), ForeColor, BackColor);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Point(x2, y), ForeColor, BackColor);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}

