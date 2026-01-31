using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SoundShelf.Models;

namespace SoundShelf.Services
{
    public class ItunesPreviewService
    {
        public async Task<List<PreviewTrack>> GetAlbumPreviewsAsync(Album album, int limit = 50)
        {
            if (album == null) return new List<PreviewTrack>();

            string artist = (album.Artist ?? "").Trim();
            string title = (album.Title ?? "").Trim();

            if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(title))
                return new List<PreviewTrack>();

            // Search tracks using artist + album title
            string term = Uri.EscapeDataString($"{artist} {title}");
            string url = $"https://itunes.apple.com/search?term={term}&entity=song&limit={limit}";

            using HttpClient client = new();
            string json = await client.GetStringAsync(url);

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement results = doc.RootElement.GetProperty("results");

            List<PreviewTrack> strict = new();
            List<PreviewTrack> fallback = new();

            // Normalized inputs for matching
            string artistN = Normalize(artist);
            string titleN = Normalize(title);

            foreach (var item in results.EnumerateArray())
            {
                if (!item.TryGetProperty("previewUrl", out var p)) continue;
                string preview = p.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(preview)) continue;

                string artistName = item.TryGetProperty("artistName", out var a) ? (a.GetString() ?? "") : "";
                string trackName = item.TryGetProperty("trackName", out var t) ? (t.GetString() ?? "") : "";
                string collectionName = item.TryGetProperty("collectionName", out var c) ? (c.GetString() ?? "") : "";

                var track = new PreviewTrack
                {
                    PreviewUrl = preview,
                    ArtistName = artistName,
                    TrackName = trackName
                };

                // Always collect for fallback
                fallback.Add(track);

                // Stricter match
                string artistNameN = Normalize(artistName);
                string collectionNameN = Normalize(collectionName);

                bool artistMatch = string.IsNullOrWhiteSpace(artistN) || artistNameN.Contains(artistN);
                bool albumMatch = string.IsNullOrWhiteSpace(titleN) || collectionNameN.Contains(titleN);

                if (artistMatch && albumMatch)
                    strict.Add(track);
            }

            var chosen = strict.Count > 0 ? strict : fallback;

            // Deduplicate by preview URL while keeping order
            List<PreviewTrack> deduped = new();
            HashSet<string> seen = new();
            foreach (var tr in chosen)
            {
                if (!string.IsNullOrWhiteSpace(tr.PreviewUrl) && seen.Add(tr.PreviewUrl))
                    deduped.Add(tr);
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
