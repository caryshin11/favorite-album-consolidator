using System;
using System.Collections.Generic;
using System.Text;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Favorite_Album_Consolidator
{
    public static class ImageBlur
    {
        public static Bitmap QuickBlur(Bitmap src, int factor = 10)
        {
            int w = Math.Max(1, src.Width / factor);
            int h = Math.Max(1, src.Height / factor);

            using Bitmap small = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(src, 0, 0, w, h);
            }

            Bitmap blurred = new Bitmap(src.Width, src.Height);
            using (Graphics g = Graphics.FromImage(blurred))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(small, 0, 0, src.Width, src.Height);
            }
            return blurred;
        }
    }
}
