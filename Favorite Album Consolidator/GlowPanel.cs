using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace Favorite_Album_Consolidator
{
    public class GlowPanel : Panel
    {
        [DefaultValue(typeof(Color), "Transparent")]
        public Color BorderColor { get; set; } = Color.Transparent;

        [DefaultValue(2)]
        public int BorderThickness { get; set; } = 2;

        public GlowPanel()
        {
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (BorderThickness <= 0) return;

            using var pen = new Pen(BorderColor, BorderThickness);
            pen.Alignment = System.Drawing.Drawing2D.PenAlignment.Inset;

            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            e.Graphics.DrawRectangle(pen, rect);
        }
    }
}
