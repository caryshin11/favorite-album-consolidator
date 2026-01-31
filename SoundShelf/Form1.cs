using SoundShelf.Models;
using SoundShelf.Services;
using System.Drawing;
using WMPLib;
using System.Linq;

namespace SoundShelf
{
    public partial class Form1 : Form
    {
        TextBox txtSearch = new();
        Button btnSearch = new();
        Button btnSave = new();
        Button btnLoad = new();
        Button btnExport = new();
        FlowLayoutPanel pnlResults = new();
        TableLayoutPanel tblGrid = new();
        Panel searchBoxPanel = new();
        Label lblSearchPlaceholder = new();

        PictureBox? selectedBox;

        // For drag and dropping
        private Point _dragStartPoint;
        private Album? _dragAlbum;

        // For hover glow effect
        private readonly Color TileNormalBorder = Color.Transparent;
        private readonly Color TileHoverBorder = Color.FromArgb(255, 170, 120, 255); // color of glow

        // Error handling when mouse flickers too fast
        private readonly List<GlowPanel> _gridCells = new();
        private readonly System.Windows.Forms.Timer _hoverFixTimer = new();

        // Audio preview
        private readonly WindowsMediaPlayer _player = new WindowsMediaPlayer();
        private readonly ItunesPreviewService _previewService = new ItunesPreviewService();
        private readonly Random _rng = new Random();
        private readonly AlbumPreviewOverlayPlayer _overlayPlayer =
    new AlbumPreviewOverlayPlayer(new ItunesPreviewService(), volume: 80);

        // Hosting the grid size + splitting the results panel and grid
        Panel gridHost = new();
        SplitContainer split = new();

        // Enable dark mode for scrollers
        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string? pszSubIdList);
        void EnableDarkScrollbars(Control control)
        {
            SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
        }

        // constants for label area ---
        private const int GridLabelHeight = 30;

        public Form1()
        {
            InitializeComponent();
            BackColor = Color.FromArgb(31, 31, 31);
            Text = "SoundShelf";
            Width = 1200;
            Height = 800;

            InitializeUI();

            // Set audio volume
            _player.settings.volume = 80;
            _player.settings.autoStart = false;

            // Enable dark scroll bars after initialization
            this.Shown += (s, e) =>
            {
                EnableDarkScrollbars(pnlResults);
            };

            CreateGrid();

            // Fix stuck hover when mouse moves very fast (MouseLeave can be missed)
            _hoverFixTimer.Interval = 30;
            _hoverFixTimer.Tick += (s, e) => FixGridHover();
            _hoverFixTimer.Start();

            // Resize square grid after creating
            this.Shown += (s, e) => split.SplitterDistance = 300;
            gridHost.Resize += (s, e) => LayoutSquareGrid();
            this.Shown += (s, e) => LayoutSquareGrid();
            LayoutSquareGrid();
        }

        void InitializeUI()
        {
            // Search container 
            searchBoxPanel.Width = 300;
            searchBoxPanel.Height = 55;
            searchBoxPanel.BackColor = Color.FromArgb(64, 65, 79); // same as textbox background
            searchBoxPanel.Margin = new Padding(0);

            // TextBox fills the panel
            txtSearch.Dock = DockStyle.Fill;
            txtSearch.Multiline = true;
            txtSearch.Font = new Font("Segoe UI", 14F);
            txtSearch.BackColor = Color.FromArgb(22, 22, 22);
            txtSearch.ForeColor = Color.FromArgb(236, 236, 241);
            txtSearch.BorderStyle = BorderStyle.FixedSingle;

            // Placeholder label overlays the textbox
            lblSearchPlaceholder.Dock = DockStyle.Fill;
            lblSearchPlaceholder.Text = "type to look for music";
            lblSearchPlaceholder.Font = new Font("Segoe UI", 10F, FontStyle.Italic);
            lblSearchPlaceholder.ForeColor = Color.FromArgb(150, 150, 160);
            lblSearchPlaceholder.BackColor = Color.FromArgb(22, 22, 22);
            lblSearchPlaceholder.TextAlign = ContentAlignment.MiddleLeft;
            lblSearchPlaceholder.Padding = new Padding(8, 0, 0, 0); // tweak to position nicely
            lblSearchPlaceholder.Cursor = Cursors.IBeam;

            // Clicking the placeholder focuses the textbox
            lblSearchPlaceholder.Click += (s, e) => txtSearch.Focus();

            // Show/hide placeholder automatically
            void UpdatePlaceholder()
            {
                lblSearchPlaceholder.Visible = string.IsNullOrWhiteSpace(txtSearch.Text);
            }
            txtSearch.TextChanged += (s, e) => UpdatePlaceholder();
            txtSearch.GotFocus += (s, e) => UpdatePlaceholder();
            txtSearch.LostFocus += (s, e) => UpdatePlaceholder();

            // Build the stack: TextBox first, label last (on top)
            searchBoxPanel.Controls.Clear();
            searchBoxPanel.Controls.Add(txtSearch);
            searchBoxPanel.Controls.Add(lblSearchPlaceholder);
            lblSearchPlaceholder.BringToFront();

            // Initial visibility
            UpdatePlaceholder();


            // --- Buttons ---
            btnSearch.Text = "Search";
            btnSave.Text = "Save";
            btnLoad.Text = "Load";
            btnExport.Text = "Export to PNG";

            btnSearch.Size = new Size(170, 55);
            btnSave.Size = new Size(170, 55);
            btnLoad.Size = new Size(170, 55);
            btnExport.Size = new Size(270, 55);

            btnSearch.Font = new Font("Segoe UI", 12F);
            btnSave.Font = new Font("Segoe UI", 12F);
            btnLoad.Font = new Font("Segoe UI", 12F);
            btnExport.Font = new Font("Segoe UI Semibold", 12F);

            btnSearch.Click += BtnSearch_Click;
            btnSave.Click += BtnSave_Click;
            btnLoad.Click += BtnLoad_Click;
            btnExport.Click += BtnExport_Click;

            btnSearch.BackColor = Color.FromArgb(22, 22, 22);
            btnSearch.ForeColor = Color.FromArgb(236, 236, 241);
            btnSave.BackColor = Color.FromArgb(22, 22, 22);
            btnSave.ForeColor = Color.FromArgb(236, 236, 241);
            btnLoad.BackColor = Color.FromArgb(22, 22, 22);
            btnLoad.ForeColor = Color.FromArgb(236, 236, 241);
            btnExport.BackColor = Color.FromArgb(22, 22, 22);
            btnExport.ForeColor = Color.FromArgb(236, 236, 241);

            // --- Top bar (stays above everything) ---
            FlowLayoutPanel topBar = new()
            {
                Dock = DockStyle.Top,
                Height = 80,
                Padding = new Padding(8),
                WrapContents = false
            };
            topBar.Controls.AddRange(new Control[] { searchBoxPanel, btnSearch, btnSave, btnLoad, btnExport });

            // ENTER triggers search
            this.AcceptButton = btnSearch;

            // --- Split layout (left results, right grid) ---
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Vertical;
            split.SplitterWidth = 6;
            split.SplitterDistance = 300;       // left panel width
            split.FixedPanel = FixedPanel.Panel1;
            split.Panel1MinSize = 220;

            // Left: results panel
            pnlResults.Dock = DockStyle.Fill;
            pnlResults.AutoScroll = true;
            pnlResults.Padding = new Padding(0, 75, 0, 0);
            pnlResults.BackColor = Color.FromArgb(22, 22, 22);
            pnlResults.BorderStyle = BorderStyle.None;
            pnlResults.AllowDrop = true;
            pnlResults.DragEnter += Results_DragEnter;
            pnlResults.DragDrop += Results_DragDrop;

            split.Panel1.Controls.Clear();
            split.Panel1.Controls.Add(pnlResults);

            // Right: grid host containing the grid
            gridHost.Dock = DockStyle.Fill;
            gridHost.BackColor = Color.FromArgb(31, 31, 31);
            gridHost.Controls.Clear();
            gridHost.Controls.Add(tblGrid);

            split.Panel2.Controls.Clear();
            split.Panel2.Controls.Add(gridHost);

            // Add to form in correct order
            Controls.Clear();
            Controls.Add(topBar);
            Controls.Add(split);
        }

        void LayoutSquareGrid()
        {
            int outerMargin = 10;

            // Account for the grid's top padding 
            int padL = tblGrid.Padding.Left;
            int padR = tblGrid.Padding.Right;
            int padT = tblGrid.Padding.Top;
            int padB = tblGrid.Padding.Bottom;

            int hostW = Math.Max(0, gridHost.ClientSize.Width - outerMargin - padL - padR);
            int hostH = Math.Max(0, gridHost.ClientSize.Height - outerMargin - padT - padB);

            // Each row needs: cover(cell) + label height
            int cellByWidth = hostW / 5;
            int cellByHeight = (hostH / 5) - GridLabelHeight;

            int cell = Math.Max(10, Math.Min(cellByWidth, cellByHeight));

            int gridW = cell * 5 + padL + padR;
            int gridH = (cell + GridLabelHeight) * 5 + padT + padB;

            tblGrid.Width = gridW;
            tblGrid.Height = gridH;

            // Force absolute sizing so cover stays square
            if (tblGrid.ColumnStyles.Count != 5 || tblGrid.RowStyles.Count != 5)
            {
                tblGrid.ColumnStyles.Clear();
                tblGrid.RowStyles.Clear();
                for (int i = 0; i < 5; i++)
                {
                    tblGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, cell));
                    tblGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, cell + GridLabelHeight));
                }
            }
            else
            {
                for (int i = 0; i < 5; i++)
                {
                    tblGrid.ColumnStyles[i].SizeType = SizeType.Absolute;
                    tblGrid.ColumnStyles[i].Width = cell;

                    tblGrid.RowStyles[i].SizeType = SizeType.Absolute;
                    tblGrid.RowStyles[i].Height = cell + GridLabelHeight;
                }
            }

            // Center and clamp (never negative)
            tblGrid.Left = Math.Max(0, (gridHost.ClientSize.Width - gridW) / 2);
            tblGrid.Top = Math.Max(0, (gridHost.ClientSize.Height - gridH) / 2);
        }

        // helper to get the PictureBox inside a grid cell panel ---
        private PictureBox GetCellPictureBox(Control cellPanel)
            => cellPanel.Controls.OfType<PictureBox>().First();

        // helper to get Label inside a grid cell panel ---
        private MarqueeLabel GetCellLabel(Control cellPanel)
            => cellPanel.Controls.OfType<MarqueeLabel>().First();

        // update the text label based on the PictureBox.Tag ---
        private void UpdateCellCaption(Control cellPanel)
        {
            var pb = GetCellPictureBox(cellPanel);
            var lbl = GetCellLabel(cellPanel);

            if (pb.Tag is Album a)
                lbl.Text = $"{a.Artist} - {a.Title}";
            else
                lbl.Text = "";
        }

        void CreateGrid()
        {
            tblGrid.Dock = DockStyle.None;
            tblGrid.Anchor = AnchorStyles.None;
            tblGrid.Margin = new Padding(0);
            tblGrid.Padding = new Padding(0, 100, 0, 0);
            tblGrid.BackColor = Color.FromArgb(31, 31, 31);

            tblGrid.RowCount = 5;
            tblGrid.ColumnCount = 5;

            tblGrid.RowStyles.Clear();
            tblGrid.ColumnStyles.Clear();

            // Percent-based; labels are inside each cell panel
            for (int i = 0; i < 5; i++)
            {
                tblGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 15f));
                tblGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15f));
            }

            tblGrid.Controls.Clear();

            for (int i = 0; i < 25; i++)
            {
                // Each cell is a panel containing PictureBox + Label
                GlowPanel cell = new()
                {
                    BorderColor = TileNormalBorder,
                    BorderThickness = 2,
                    Dock = DockStyle.Fill,
                    BackColor = Color.FromArgb(20, 20, 25),
                    Margin = new Padding(2),
                    Padding = new Padding(4)
                };

                _gridCells.Add(cell);

                MarqueeLabel lbl = new()
                {
                    Dock = DockStyle.Bottom,
                    Height = GridLabelHeight,
                    ForeColor = Color.White,
                    BackColor = Color.FromArgb(20, 20, 20),
                    Font = new Font("Consolas", 8F)
                };

                PictureBox pb = new()
                {
                    Dock = DockStyle.Fill,
                    BorderStyle = BorderStyle.FixedSingle,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    AllowDrop = true,
                    BackColor = Color.Black
                };

                void SetHover(bool on)
                {
                    cell.BorderColor = on ? TileHoverBorder : TileNormalBorder;
                    cell.Invalidate();
                }

                // drag/drop and selection
                pb.MouseDown += Grid_MouseDown;
                pb.MouseMove += (s, e) =>
                {
                    if (e.Button != MouseButtons.Left) return;
                    if (_dragStartPoint == Point.Empty) return;

                    int dx = Math.Abs(e.X - _dragStartPoint.X);
                    int dy = Math.Abs(e.Y - _dragStartPoint.Y);

                    if (dx >= SystemInformation.DragSize.Width / 2 ||
                        dy >= SystemInformation.DragSize.Height / 2)
                    {
                        pb.DoDragDrop(pb, DragDropEffects.Move);
                        _dragStartPoint = Point.Empty;
                    }
                };
                pb.MouseUp += (s, e) =>
                {
                    _dragStartPoint = Point.Empty;
                };
                pb.DragEnter += Grid_DragEnter;
                pb.DragDrop += Grid_DragDrop;
                pb.Click += (s, e) => SelectBox(pb);

                ContextMenuStrip menu = new();
                menu.Items.Add("Delete", null, (s, e) => ClearBox(pb));
                pb.ContextMenuStrip = menu;

                cell.Controls.Add(pb);
                cell.Controls.Add(lbl);
                tblGrid.Controls.Add(cell);
                _overlayPlayer.AttachOverlay(cell, pb, SetHover);
            }
        }

        async void BtnSearch_Click(object? sender, EventArgs e)
        {
            string q = txtSearch.Text.Trim();

            if (string.IsNullOrWhiteSpace(q))
            {
                // Optional: clear results or just do nothing
                pnlResults.Controls.Clear();
                return;
            }

            pnlResults.Controls.Clear();

            try
            {
                var service = new LastFmSearchService();
                var albums = await service.SearchAsync(q);

                foreach (var album in albums)
                    pnlResults.Controls.Add(CreateResultBox(album));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Search failed: " + ex.Message, "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void BtnSave_Click(object? sender, EventArgs e)
        {
            using SaveFileDialog dialog = new()
            {
                Filter = "Album Layout (*.json)|*.json",
                DefaultExt = "json"
            };

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                JsonSaveService.Save(dialog.FileName, tblGrid);
            }
        }

        void BtnLoad_Click(object? sender, EventArgs e)
        {
            using OpenFileDialog dialog = new()
            {
                Filter = "Album Layout (*.json)|*.json",
                DefaultExt = "json"
            };

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                JsonSaveService.Load(dialog.FileName, tblGrid);

                // after load, refresh captions for all cells
                foreach (Control cell in tblGrid.Controls)
                    UpdateCellCaption(cell);
            }
        }

        void BtnExport_Click(object? sender, EventArgs e)
        {
            using SaveFileDialog dialog = new()
            {
                Filter = "PNG Image (*.png)|*.png",
                DefaultExt = "png",
            };

            if (dialog.ShowDialog() != DialogResult.OK)
                return;

            ExportGridPng(dialog.FileName);
        }

        PictureBox CreateResultBox(Album album)
        {
            PictureBox pb = new()
            {
                Width = 140,
                Height = 140,
                SizeMode = PictureBoxSizeMode.Zoom,
                ImageLocation = album.ImageUrl,
                Tag = album,
                Cursor = Cursors.Hand
            };

            pb.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                _dragStartPoint = e.Location;
                _dragAlbum = album;
            };

            pb.MouseMove += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                if (_dragAlbum == null) return;

                // Only start drag after the mouse moved a bit
                int dx = Math.Abs(e.X - _dragStartPoint.X);
                int dy = Math.Abs(e.Y - _dragStartPoint.Y);

                if (dx >= SystemInformation.DragSize.Width / 2 ||
                    dy >= SystemInformation.DragSize.Height / 2)
                {
                    pb.DoDragDrop(_dragAlbum, DragDropEffects.Copy);
                    _dragAlbum = null;
                }
            };

            pb.MouseUp += (s, e) => _dragAlbum = null;

            return pb;
        }

        void Results_DragEnter(object? sender, DragEventArgs e)
        {
            if (e.Data == null) return;

            // Only accept PictureBox drags that came from the grid (move = delete from grid)
            if (e.Data.GetDataPresent(typeof(PictureBox)))
            {
                var pb = e.Data.GetData(typeof(PictureBox)) as PictureBox;

                bool fromGrid =
                    pb != null &&
                    pb.Parent != null &&
                    tblGrid.Controls.Contains(pb.Parent); // pb is inside a grid cell panel

                e.Effect = fromGrid ? DragDropEffects.Move : DragDropEffects.None;
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }

        void Results_DragDrop(object? sender, DragEventArgs e)
        {
            if (e.Data == null) return;

            if (!e.Data.GetDataPresent(typeof(PictureBox))) return;

            var pb = e.Data.GetData(typeof(PictureBox)) as PictureBox;
            if (pb == null) return;

            // Only delete if it came from the grid
            if (pb.Parent != null && tblGrid.Controls.Contains(pb.Parent))
            {
                // This already clears image + tag + caption (in your updated version)
                ClearBox(pb);

                // Optional: clear selection highlight
                if (selectedBox == pb) selectedBox = null;
            }
        }

        void Grid_MouseDown(object? sender, MouseEventArgs e)
        {
            if (sender is not PictureBox pb) return;
            if (e.Button != MouseButtons.Left) return;
            if (pb.Tag is not Album) return;

            _dragStartPoint = e.Location;
            _dragAlbum = null; // we drag the PictureBox itself, not Album
        }

        void Grid_DragEnter(object? sender, DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(typeof(Album)))
                e.Effect = DragDropEffects.Copy;
            else if (e.Data != null && e.Data.GetDataPresent(typeof(PictureBox)))
                e.Effect = DragDropEffects.Move;
        }

        void Grid_DragDrop(object? sender, DragEventArgs e)
        {
            if (sender is not PictureBox target || e.Data == null) return;

            // Drop from results
            if (e.Data.GetDataPresent(typeof(Album)))
            {
                Album album = (Album)e.Data.GetData(typeof(Album))!;
                target.Tag = album;
                target.ImageLocation = album.ImageUrl;

                // update caption on this cell
                UpdateCellCaption(target.Parent!);
                return;
            }
            // Swap within grid
            else if (e.Data.GetDataPresent(typeof(PictureBox)))
            {
                var source = (PictureBox)e.Data.GetData(typeof(PictureBox))!;
                if (source != target)
                {
                    SwapBoxes(source, target);

                    // update both captions
                    UpdateCellCaption(source.Parent!);
                    UpdateCellCaption(target.Parent!);
                }
            }
        }

        private void FixGridHover()
        {
            // If grid isn't visible yet, bail
            if (!tblGrid.IsHandleCreated || !tblGrid.Visible) return;

            // Mouse position in tblGrid coordinates
            Point p = tblGrid.PointToClient(Control.MousePosition);

            // If not inside the grid bounds, clear all
            if (!tblGrid.ClientRectangle.Contains(p))
            {
                foreach (var cell in _gridCells)
                {
                    cell.BorderColor = TileNormalBorder;
                    cell.Invalidate();
                }
                return;
            }

            // Find which GlowPanel cell is under the mouse
            Control? hit = tblGrid.GetChildAtPoint(p);
            GlowPanel? hoveredCell = null;

            // GetChildAtPoint sometimes returns inner controls; walk up to GlowPanel
            while (hit != null)
            {
                if (hit is GlowPanel gp)
                {
                    hoveredCell = gp;
                    break;
                }
                hit = hit.Parent;
                if (hit == tblGrid) break;
            }

            // Apply glow only to the hovered cell
            foreach (var cell in _gridCells)
            {
                bool on = (cell == hoveredCell);
                var desired = on ? TileHoverBorder : TileNormalBorder;
                if (cell.BorderColor != desired)
                {
                    cell.BorderColor = desired;
                    cell.Invalidate();
                }
            }
        }


        void SwapBoxes(PictureBox a, PictureBox b)
        {
            (a.Tag, b.Tag) = (b.Tag, a.Tag);
            (a.ImageLocation, b.ImageLocation) = (b.ImageLocation, a.ImageLocation);
        }

        void ClearBox(PictureBox pb)
        {
            pb.Tag = null;
            pb.Image = null;
            pb.ImageLocation = null;

            UpdateCellCaption(pb.Parent!);
        }

        void SelectBox(PictureBox pb)
        {
            // "selected" highlight only on pictureboxes
            foreach (Control cell in tblGrid.Controls)
            {
                var cover = GetCellPictureBox(cell);
                cover.BackColor = Color.Black;
            }

            selectedBox = pb;
            pb.BackColor = Color.LightBlue;
        }

        void ExportGridPng(string path)
        {
            const int TargetW = 1395;
            const int TargetH = 1595;

            // Export-only label bump (tweak if you want)
            const float ExportLabelFontSize = 11.5f;
            const int ExportLabelHeight = 40;

            // --- Save current state ---
            int oldW = tblGrid.Width;
            int oldH = tblGrid.Height;

            Padding oldPadding = tblGrid.Padding;
            Padding oldMargin = tblGrid.Margin;
            var oldDock = tblGrid.Dock;
            var oldAnchor = tblGrid.Anchor;

            // Save row/col SizeType + size
            int colCount = tblGrid.ColumnStyles.Count;
            int rowCount = tblGrid.RowStyles.Count;

            SizeType[] oldColTypes = new SizeType[colCount];
            float[] oldColSizes = new float[colCount];

            SizeType[] oldRowTypes = new SizeType[rowCount];
            float[] oldRowSizes = new float[rowCount];

            for (int i = 0; i < colCount; i++)
            {
                oldColTypes[i] = tblGrid.ColumnStyles[i].SizeType;
                oldColSizes[i] = tblGrid.ColumnStyles[i].Width;
            }

            for (int i = 0; i < rowCount; i++)
            {
                oldRowTypes[i] = tblGrid.RowStyles[i].SizeType;
                oldRowSizes[i] = tblGrid.RowStyles[i].Height;
            }

            // Save label font + height so we can restore
            List<Label> labels = new();
            Dictionary<Label, Font> oldFonts = new();
            Dictionary<Label, int> oldHeights = new();

            foreach (Control cell in tblGrid.Controls)
            {
                foreach (Control c in cell.Controls)
                {
                    if (c is Label lbl)
                    {
                        labels.Add(lbl);
                        oldFonts[lbl] = lbl.Font;
                        oldHeights[lbl] = lbl.Height;
                    }
                }
            }

            try
            {
                // --- TEMP export layout: remove UI padding/margins so export is exact ---
                tblGrid.Padding = Padding.Empty;
                tblGrid.Margin = Padding.Empty;
                tblGrid.Dock = DockStyle.None;
                tblGrid.Anchor = AnchorStyles.None;

                // --- TEMP: make labels bigger for export ---
                foreach (var lbl in labels)
                {
                    lbl.Font = new Font(lbl.Font.FontFamily, ExportLabelFontSize, lbl.Font.Style);
                    lbl.Height = ExportLabelHeight;
                }

                // --- Force exact fixed output size ---
                tblGrid.Width = TargetW;
                tblGrid.Height = TargetH;

                // --- Force absolute sizing with NO stretching ---
                // Column widths must sum to TargetW exactly
                int baseCol = TargetW / 5;
                int extraCols = TargetW % 5; // distribute remainder

                // Row heights must sum to TargetH exactly
                // Each row = cover + label
                // cover height = rowHeight - labelHeight
                int baseRow = TargetH / 5;
                int extraRows = TargetH % 5;

                tblGrid.ColumnStyles.Clear();
                tblGrid.RowStyles.Clear();

                for (int i = 0; i < 5; i++)
                {
                    int w = baseCol + (i < extraCols ? 1 : 0);
                    tblGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, w));
                }

                for (int i = 0; i < 5; i++)
                {
                    int h = baseRow + (i < extraRows ? 1 : 0);
                    tblGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
                }

                // IMPORTANT:
                // Make sure your PictureBox stays square within the cell by keeping your cell layout:
                // pb.Dock = Fill, label.Dock = Bottom with ExportLabelHeight.
                // The cover will become (rowHeight - labelHeight) tall; if you want perfect squares,
                // choose TargetW/5 == (TargetH/5 - ExportLabelHeight).

                tblGrid.PerformLayout();
                tblGrid.Refresh();

                using Bitmap bmp = new(TargetW, TargetH);
                tblGrid.DrawToBitmap(bmp, new Rectangle(0, 0, TargetW, TargetH));
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            finally
            {
                // --- Restore labels ---
                foreach (var lbl in labels)
                {
                    lbl.Font = oldFonts[lbl];
                    lbl.Height = oldHeights[lbl];
                }

                // --- Restore grid layout ---
                tblGrid.Width = oldW;
                tblGrid.Height = oldH;

                tblGrid.Padding = oldPadding;
                tblGrid.Margin = oldMargin;
                tblGrid.Dock = oldDock;
                tblGrid.Anchor = oldAnchor;

                // Restore styles exactly
                tblGrid.ColumnStyles.Clear();
                tblGrid.RowStyles.Clear();

                for (int i = 0; i < oldColTypes.Length; i++)
                    tblGrid.ColumnStyles.Add(new ColumnStyle(oldColTypes[i], oldColSizes[i]));

                for (int i = 0; i < oldRowTypes.Length; i++)
                    tblGrid.RowStyles.Add(new RowStyle(oldRowTypes[i], oldRowSizes[i]));

                tblGrid.PerformLayout();
                tblGrid.Refresh();

                LayoutSquareGrid();
            }
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _overlayPlayer.Dispose();
            base.OnFormClosed(e);
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Delete && selectedBox != null)
            {
                ClearBox(selectedBox);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}