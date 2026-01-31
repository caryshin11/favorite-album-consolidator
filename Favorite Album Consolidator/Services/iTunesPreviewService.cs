using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Favorite_Album_Consolidator.Models;

namespace Favorite_Album_Consolidator.Services
{
    public class ItunesPreviewService
    {
        // Reuse ONE HttpClient
        private static readonly HttpClient _http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient();
            // Some endpoints behave better with a UA
            c.DefaultRequestHeaders.UserAgent.ParseAdd("FavoriteAlbumConsolidator/1.0");
            return c;
        }

        public async Task<List<string>> GetAlbumPreviewUrlsAsync(Album album, int limit = 50)
        {
            if (album == null) return new List<string>();

            string artist = (album.Artist ?? "").Trim();
            string title = (album.Title ?? "").Trim();

            if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(title))
                return new List<string>();

            // Find the best matching ALBUM
            long? collectionId = await FindBestAlbumCollectionIdAsync(artist, title);
            if (collectionId == null)
            {
                // Fallback: original "song search" approach
                return await FallbackSongSearchAsync(artist, title, limit);
            }

            // Lookup tracks for that album (collectionId)
            string lookupUrl =
                $"https://itunes.apple.com/lookup?id={collectionId.Value}&entity=song&limit={limit}&country=US";

            string lookupJson = await _http.GetStringAsync(lookupUrl);

            using var doc = JsonDocument.Parse(lookupJson);
            if (!doc.RootElement.TryGetProperty("results", out var results))
                return new List<string>();

            // First item is the collection info; songs follow
            var urls = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in results.EnumerateArray())
            {
                // Only track items have previewUrl
                if (item.TryGetProperty("previewUrl", out var p))
                {
                    var u = p.GetString();
                    if (!string.IsNullOrWhiteSpace(u) && seen.Add(u))
                        urls.Add(u);
                }
            }

            return urls;
        }

        private async Task<long?> FindBestAlbumCollectionIdAsync(string artist, string title)
        {
            string term = Uri.EscapeDataString($"{artist} {title}");
            string url =
                $"https://itunes.apple.com/search?term={term}&entity=album&limit=10&country=US";

            string json = await _http.GetStringAsync(url);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results))
                return null;

            string artistN = Normalize(artist);
            string titleN = Normalize(title);

            long? bestId = null;
            int bestScore = int.MinValue;

            foreach (var item in results.EnumerateArray())
            {
                string artistName = item.TryGetProperty("artistName", out var a) ? (a.GetString() ?? "") : "";
                string collectionName = item.TryGetProperty("collectionName", out var c) ? (c.GetString() ?? "") : "";

                // score match
                int score = 0;
                string aN = Normalize(artistName);
                string cN = Normalize(collectionName);

                if (!string.IsNullOrWhiteSpace(artistN))
                {
                    if (aN == artistN) score += 5;
                    else if (aN.Contains(artistN) || artistN.Contains(aN)) score += 2;
                    else score -= 3;
                }

                if (!string.IsNullOrWhiteSpace(titleN))
                {
                    if (cN == titleN) score += 6;
                    else if (cN.Contains(titleN) || titleN.Contains(cN)) score += 3;
                    else score -= 2;
                }

                if (item.TryGetProperty("collectionId", out var idProp) && idProp.TryGetInt64(out var id))
                {
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestId = id;
                    }
                }
            }

            return bestId;
        }

        private async Task<List<string>> FallbackSongSearchAsync(string artist, string title, int limit)
        {
            string term = Uri.EscapeDataString($"{artist} {title}");
            string url =
                $"https://itunes.apple.com/search?term={term}&entity=song&limit={limit}&country=US";

            string json = await _http.GetStringAsync(url);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results))
                return new List<string>();

            var urls = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in results.EnumerateArray())
            {
                if (item.TryGetProperty("previewUrl", out var p))
                {
                    var u = p.GetString();
                    if (!string.IsNullOrWhiteSpace(u) && seen.Add(u))
                        urls.Add(u);
                }
            }

            return urls;
        }

        // Same Normalize 
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
            }

            return string.Join(" ",
                sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
