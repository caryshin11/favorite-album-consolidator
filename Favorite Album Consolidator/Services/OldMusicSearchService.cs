using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Favorite_Album_Consolidator.Models;

namespace Favorite_Album_Consolidator.Services
{
    public class OldMusicSearchService
    {
        public async Task<List<Album>> SearchAsync(string query)
        {
            using HttpClient client = new();
            string url = $"https://itunes.apple.com/search?term={query}&entity=album&limit=75";

            string json = await client.GetStringAsync(url);
            JsonDocument doc = JsonDocument.Parse(json);

            List<Album> results = new();

            foreach (var item in doc.RootElement.GetProperty("results").EnumerateArray())
            {
                results.Add(new Album
                {
                    Artist = item.GetProperty("artistName").GetString() ?? "",
                    Title = item.GetProperty("collectionName").GetString() ?? "",
                    ImageUrl = item.GetProperty("artworkUrl100").GetString() ?? ""
                });
            }

            return results;
        }
    }
}

