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

            using HttpClient client = new();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FavoriteAlbumConsolidator/1.0");

            string artistN = Normalize(artist);
            string titleN = Normalize(title);

            // 1) Search ALBUMS (not songs) to get the correct collectionId
            string albumTerm = Uri.EscapeDataString($"{artist} {title}");
            string albumSearchUrl = $"https://itunes.apple.com/search?term={albumTerm}&entity=album&limit=10&country=US";
            string albumJson = await client.GetStringAsync(albumSearchUrl);

            using JsonDocument albumDoc = JsonDocument.Parse(albumJson);
            JsonElement albumResults = albumDoc.RootElement.GetProperty("results");

            long? bestCollectionId = null;
            int bestScore = int.MinValue;

            foreach (var item in albumResults.EnumerateArray())
            {
                string artistName = item.TryGetProperty("artistName", out var a) ? (a.GetString() ?? "") : "";
                string collectionName = item.TryGetProperty("collectionName", out var c) ? (c.GetString() ?? "") : "";

                string aN = Normalize(artistName);
                string cN = Normalize(collectionName);

                int score = 0;

                // Strongly prefer exact album title matches
                if (!string.IsNullOrWhiteSpace(titleN))
                {
                    if (cN == titleN) score += 20;
                    else if (cN.StartsWith(titleN)) score += 12; 
                    else if (cN.Contains(titleN)) score += 6;
                    else score -= 10;
                }

                // Strongly prefer exact artist matches
                if (!string.IsNullOrWhiteSpace(artistN))
                {
                    if (aN == artistN) score += 15;
                    else if (aN.Contains(artistN) || artistN.Contains(aN)) score += 6;
                    else score -= 10;
                }

                // Penalize common “wrong-ish” variants unless user typed them
                if (!titleN.Contains("deluxe") && cN.Contains("deluxe")) score -= 4;
                if (!titleN.Contains("remaster") && (cN.Contains("remaster") || cN.Contains("remastered"))) score -= 4;
                if (!titleN.Contains("edition") && cN.Contains("edition")) score -= 2;

                if (item.TryGetProperty("collectionId", out var idProp) && idProp.TryGetInt64(out var id))
                {
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestCollectionId = id;
                    }
                }
            }

            if (bestCollectionId == null)
                return new List<string>(); // no good album match found

            // 2) Lookup tracks for that album collectionId (this is the key)
            string lookupUrl = $"https://itunes.apple.com/lookup?id={bestCollectionId.Value}&entity=song&limit={limit}&country=US";
            string lookupJson = await client.GetStringAsync(lookupUrl);

            using JsonDocument lookupDoc = JsonDocument.Parse(lookupJson);
            JsonElement lookupResults = lookupDoc.RootElement.GetProperty("results");

            // Collect previewUrls from tracks (skip duplicates)
            List<string> deduped = new();
            HashSet<string> seen = new();

            foreach (var item in lookupResults.EnumerateArray())
            {
                if (item.TryGetProperty("previewUrl", out var p))
                {
                    string u = p.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(u) && seen.Add(u))
                        deduped.Add(u);
                }
            }

            return deduped;
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
