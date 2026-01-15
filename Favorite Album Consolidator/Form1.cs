using System.Drawing;

namespace Favorite_Album_Consolidator
{
    public partial class Form1 : Form
    {
        TableLayoutPanel tblGrid = new();
        public Form1()
        {
            InitializeComponent();
            CreateGrid();
        }


        void CreateGrid()
        {
            tblGrid.Dock = DockStyle.Fill;
            tblGrid.BackColor = Color.Gray;

            tblGrid.RowCount = 5;
            tblGrid.ColumnCount = 5;

            // IMPORTANT: make rows/cols actually take space
            tblGrid.RowStyles.Clear();
            tblGrid.ColumnStyles.Clear();

            float rowPercent = 100f / tblGrid.RowCount;
            float colPercent = 100f / tblGrid.ColumnCount;

            for (int r = 0; r < tblGrid.RowCount; r++)
                tblGrid.RowStyles.Add(new RowStyle(SizeType.Percent, rowPercent));

            for (int c = 0; c < tblGrid.ColumnCount; c++)
                tblGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, colPercent));

            tblGrid.Controls.Clear();

            for (int i = 0; i < tblGrid.RowCount * tblGrid.ColumnCount; i++)
            {
                PictureBox pb = new PictureBox
                {
                    Dock = DockStyle.Fill,
                    BorderStyle = BorderStyle.FixedSingle,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    BackColor = Color.Black,
                    Margin = new Padding(1)
                };

                tblGrid.Controls.Add(pb);
            }

            Controls.Add(tblGrid);
        }
    }
}
