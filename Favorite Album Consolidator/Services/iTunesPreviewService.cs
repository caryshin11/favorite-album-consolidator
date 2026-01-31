using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Favorite_Album_Consolidator.Models;

namespace Favorite_Album_Consolidator.Services
{
    public class ItunesPreviewService
    {
        public async Task<List<string>> GetAlbumPreviewUrlsAsync(Album album, int limit = 50)
        {
            if (album == null) return new List<string>();

            string artist = (album.Artist ?? "").Trim();
            string title = (album.Title ?? "").Trim();

            if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(title))
                return new List<string>();

            // Search tracks using artist + album title
            string term = Uri.EscapeDataString($"{artist} {title}");
            string url = $"https://itunes.apple.com/search?term={term}&entity=song&limit={limit}";

            using HttpClient client = new();
            string json = await client.GetStringAsync(url);

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement results = doc.RootElement.GetProperty("results");

            List<string> strict = new();
            List<string> fallback = new();

            // Normalized inputs for matching
            string artistN = Normalize(artist);
            string titleN = Normalize(title);

            foreach (var item in results.EnumerateArray())
            {
                // Always collect previews for fallback
                if (item.TryGetProperty("previewUrl", out var p))
                {
                    string preview = p.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(preview))
                        fallback.Add(preview);
                }

                // Try stricter matching to keep it on the right album
                string artistName = item.TryGetProperty("artistName", out var a) ? (a.GetString() ?? "") : "";
                string collectionName = item.TryGetProperty("collectionName", out var c) ? (c.GetString() ?? "") : "";

                string artistNameN = Normalize(artistName);
                string collectionNameN = Normalize(collectionName);

                bool artistMatch = string.IsNullOrWhiteSpace(artistN) || artistNameN.Contains(artistN);
                bool albumMatch = string.IsNullOrWhiteSpace(titleN) || collectionNameN.Contains(titleN);

                if (artistMatch && albumMatch)
                {
                    if (item.TryGetProperty("previewUrl", out var p2))
                    {
                        string preview2 = p2.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(preview2))
                            strict.Add(preview2);
                    }
                }
            }

            var chosen = strict.Count > 0 ? strict : fallback;

            // Deduplicate while keeping order
            List<string> deduped = new();
            HashSet<string> seen = new();
            foreach (var u in chosen)
            {
                if (seen.Add(u))
                    deduped.Add(u);
            }

            return deduped;
        }

        // Removes punctuation differences & extra whitespace, lowercases
        private static string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";

            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                if (char.IsLetterOrDigit(ch))
                    sb.Append(char.ToLowerInvariant(ch));
                else if (char.IsWhiteSpace(ch))
                    sb.Append(' ');
                // ignore punctuation/symbols
            }

            // collapse multiple spaces
            return string.Join(" ", sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
