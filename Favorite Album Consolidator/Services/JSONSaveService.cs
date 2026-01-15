using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Favorite_Album_Consolidator.Models;

namespace Favorite_Album_Consolidator.Services
{
    public static class JsonSaveService
    {
        public static void Save(string path, TableLayoutPanel grid)
        {
            List<GridItem> items = new();

            for (int i = 0; i < grid.Controls.Count; i++)
            {
                // Each control is a Panel containing a PictureBox + Label
                var cell = (Panel)grid.Controls[i];
                var pb = cell.Controls.OfType<PictureBox>().First();

                items.Add(new GridItem
                {
                    Index = i,
                    Album = pb.Tag as Album
                });
            }

            File.WriteAllText(path, JsonSerializer.Serialize(items, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }

        public static void Load(string path, TableLayoutPanel grid)
        {
            if (!File.Exists(path)) return;

            var json = File.ReadAllText(path);
            var items = JsonSerializer.Deserialize<List<GridItem>>(json);
            if (items == null) return;

            foreach (var item in items)
            {
                var cell = (Panel)grid.Controls[item.Index];
                var pb = cell.Controls.OfType<PictureBox>().First();
                var lbl = cell.Controls.OfType<MarqueeLabel>().First();

                pb.Tag = item.Album;
                pb.ImageLocation = item.Album?.ImageUrl;

                lbl.Text = item.Album == null ? "" : $"{item.Album.Artist} - {item.Album.Title}";
            }
        }
    }
}
