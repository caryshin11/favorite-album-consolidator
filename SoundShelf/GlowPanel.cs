using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SoundShelf
{
    public class GlowPanel : Panel
    {
        [DefaultValue(typeof(Color), "Transparent")]
        public Color BorderColor { get; set; } = Color.Transparent;

        [DefaultValue(2)]
        public int BorderThickness { get; set; } = 2;

        // NEW
        [DefaultValue(12)]
        public int CornerRadius { get; set; } = 12;

        public GlowPanel()
        {
            DoubleBuffered = true;

            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateRegion();
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            UpdateRegion();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // prevent default square background paint
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (r.Width <= 0 || r.Height <= 0) return;

            // Because OnPaintBackground is disabled, we MUST clear ourselves
            e.Graphics.Clear(BackColor);

            using var path = GetRoundedPath(r, CornerRadius);

            // fill background (rounded)
            using (var bg = new SolidBrush(BackColor))
                e.Graphics.FillPath(bg, path);

            // draw border glow (rounded)
            if (BorderThickness > 0 && BorderColor.A > 0)
            {
                using var pen = new Pen(BorderColor, BorderThickness)
                {
                    Alignment = PenAlignment.Inset
                };
                e.Graphics.DrawPath(pen, path);
            }
        }

        private void UpdateRegion()
        {
            if (!IsHandleCreated || Width < 2 || Height < 2) return;

            using var path = GetRoundedPath(
                new Rectangle(0, 0, Width - 1, Height - 1),
                CornerRadius
            );

            Region?.Dispose();
            Region = new Region(path);

            // also clip the PictureBox so album art follows corners
            foreach (Control c in Controls)
            {
                if (c is PictureBox pb && pb.Width > 2 && pb.Height > 2)
                {
                    using var p = GetRoundedPath(
                        new Rectangle(0, 0, pb.Width - 1, pb.Height - 1),
                        Math.Max(0, CornerRadius - 2)
                    );

                    pb.Region?.Dispose();
                    pb.Region = new Region(p);
                    break;
                }
            }
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
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;

            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();

            return path;
        }
    }
}
