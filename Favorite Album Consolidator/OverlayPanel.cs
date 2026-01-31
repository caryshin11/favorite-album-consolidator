using System;
using System.Collections.Generic;
using System.Text;
using System.Drawing;
using System.Windows.Forms;
using System.ComponentModel;

namespace Favorite_Album_Consolidator
{
    public class OverlayPanel : Panel
    {
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color OverlayColor { get; set; } = Color.FromArgb(140, 0, 0, 0);

        public OverlayPanel()
        {
            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using var b = new SolidBrush(OverlayColor);
            e.Graphics.FillRectangle(b, ClientRectangle);
            base.OnPaint(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Do nothing (prevents flicker)
        }
    }
}
