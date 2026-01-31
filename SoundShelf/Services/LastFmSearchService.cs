using System;
using System.Collections.Generic;
using System.Text;
using System.Net.Http;
using System.Text.Json;
using SoundShelf.Models;

namespace SoundShelf.Services
{
    public class LastFmSearchService
    {
        // Put your key here (or load from config later)
        private const string ApiKey = "073f233418b81f2c836ce0a807ce73af";

        public async Task<List<Album>> SearchAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<Album>();

            using HttpClient client = new();

            // URL encode user text
            string album = Uri.EscapeDataString(query);

            // Last.fm REST root + album.search method
            // Docs example: /2.0/?method=album.search&album=...&api_key=...&format=json
            string url =
                $"http://ws.audioscrobbler.com/2.0/?method=album.search&album={album}&api_key={ApiKey}&format=json&limit=25";

            string json = await client.GetStringAsync(url);
            using JsonDocument doc = JsonDocument.Parse(json);

            List<Album> results = new();

            // Response shape: results -> albummatches -> album (array)
            var albums = doc.RootElement
                .GetProperty("results")
                .GetProperty("albummatches")
                .GetProperty("album");

            foreach (var item in albums.EnumerateArray())
            {
                string title = item.GetProperty("name").GetString() ?? "";
                string artist = item.GetProperty("artist").GetString() ?? "";

                // Last.fm gives multiple image sizes in "image": [{#text, size}, ...]
                string imageUrl = "";
                if (item.TryGetProperty("image", out var images) && images.ValueKind == JsonValueKind.Array)
                {
                    // Prefer largest available (usually "extralarge" / "mega")
                    foreach (var img in images.EnumerateArray())
                    {
                        string size = img.GetProperty("size").GetString() ?? "";
                        string urlText = img.GetProperty("#text").GetString() ?? "";

                        if (!string.IsNullOrWhiteSpace(urlText))
                        {
                            imageUrl = urlText; // keep last non-empty (often largest is last)
                            if (size == "mega") break;
                        }
                    }
                }

                results.Add(new Album
                {
                    Artist = artist,
                    Title = title,
                    ImageUrl = imageUrl
                });
            }

            return results;
        }
    }
}
