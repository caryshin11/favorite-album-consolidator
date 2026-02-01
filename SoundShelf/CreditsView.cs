using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace SoundShelf
{
    public sealed class CreditsView : UserControl
    {
        private readonly Label _text = new();

        public CreditsView()
        {
            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(18, 18, 22);
            Padding = new Padding(20, 10, 20, 20);

            // Title (TOP) 
            var title = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 60,
                Text = "Music credits:",
                ForeColor = Color.FromArgb(210, 210, 220),
                Font = new Font(new FontFamily("Segoe UI"), 10.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.TopLeft,
                Margin = Padding.Empty
            };

            // Icons row
            var iconRow = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 72,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0, 6, 0, 18),
                Padding = Padding.Empty
            };
            iconRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            iconRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            iconRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            var pbLastFm = MakeIcon(@"Assets\lastfm_128.png", "LFM");
            var pbItunes = MakeIcon(@"Assets\itunes_128.png", "iT");

            pbLastFm.Click += (s, e) => OpenUrl("https://www.last.fm/");
            pbItunes.Click += (s, e) => OpenUrl("https://music.apple.com/");

            // Center within each 50% column
            pbLastFm.Anchor = AnchorStyles.None;
            pbItunes.Anchor = AnchorStyles.None;

            iconRow.Controls.Add(pbLastFm, 0, 0);
            iconRow.Controls.Add(pbItunes, 1, 0);

            // Divider
            var divider = new Panel
            {
                Dock = DockStyle.Top,
                Height = 1,
                BackColor = Color.FromArgb(60, 255, 255, 255),
                Margin = new Padding(0, 16, 0, 16)
            };

            // Text
            _text.Dock = DockStyle.Fill;
            _text.ForeColor = Color.FromArgb(235, 235, 245);
            _text.Font = new Font(new FontFamily("Segoe UI"), 9f, FontStyle.Regular);
            _text.AutoSize = false;
            _text.TextAlign = ContentAlignment.TopLeft;
            _text.Text =
                "SoundShelf is not affiliated with any of the above services.";
            _text.Padding = new Padding(0, 20, 0, 0);

            // Add Fill first
            Controls.Add(_text);
            Controls.Add(divider);
            Controls.Add(iconRow);
            Controls.Add(title);
        }

        private PictureBox MakeIcon(string relativePath, string fallbackText)
        {
            var pb = new PictureBox
            {
                Size = new Size(48, 48), // slightly bigger
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.FromArgb(30, 30, 36),
                TabStop = false,
                Margin = Padding.Empty
            };

            pb.Cursor = Cursors.Hand;

            string fullPath = Path.Combine(Application.StartupPath, relativePath);

            var img = LoadImageSafe(fullPath);
            if (img != null)
            {
                pb.Image = img;
                return pb;
            }

            // Tooltip to help debug path issues
            var tt = new ToolTip();
            tt.SetToolTip(pb, "Missing icon:\n" + fullPath);

            return pb;
        }

        private static Image? LoadImageSafe(string fullPath)
        {
            try
            {
                if (!File.Exists(fullPath))
                    return null;

                // Load into memory 
                using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                ms.Position = 0;

                return Image.FromStream(ms);
            }
            catch
            {
                return null;
            }
        }
        private static void OpenUrl(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch
            {

            }
        }
    }
}
