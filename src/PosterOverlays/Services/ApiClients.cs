using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PosterOverlays.Data;

namespace PosterOverlays.Services
{
    /// <summary>HTTP 429 from MDBList. Can be a short-term speed limit or the daily quota; the caller checks which.</summary>
    public sealed class RateLimitedException : Exception
    {
        public RateLimitedException(string message, TimeSpan? retryAfter)
            : base(message)
        {
            this.RetryAfter = retryAfter;
        }

        /// <summary>How long MDBList asked us to wait, when it said.</summary>
        public TimeSpan? RetryAfter { get; }
    }

    public sealed class MdbListQuota
    {
        public int DailyLimit { get; set; }

        public int UsedToday { get; set; }

        public DateTime CheckedUtc { get; set; }

        public int Remaining => Math.Max(0, this.DailyLimit - this.UsedToday);
    }

    internal static class Http
    {
        /// <summary>Shared client. Settable so tests can point the API clients at a fake server.</summary>
        public static HttpClient Client { get; set; } = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("EmbyPosterOverlays/0.1");
            return client;
        }
    }

    /// <summary>Minimal TMDB v3 client: series status and id lookups.</summary>
    public sealed class TmdbClient
    {
        private const string BaseUrl = "https://api.themoviedb.org/3/";
        private readonly string key;

        public TmdbClient(string key)
        {
            this.key = (key ?? string.Empty).Trim();
        }

        public bool IsConfigured => this.key.Length > 0;

        // A v4 read access token is a long JWT; a v3 API key is 32 hex characters.
        private bool UsesBearer => this.key.Length > 40;

        public async Task<StatusCacheEntry> GetSeriesStatusAsync(string tmdbId, CancellationToken ct)
        {
            using var doc = await this.GetAsync("tv/" + Uri.EscapeDataString(tmdbId), ct).ConfigureAwait(false);
            if (doc == null)
            {
                return null;
            }

            var root = doc.RootElement;
            return new StatusCacheEntry
            {
                TmdbStatus = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null,
                LastAirDate = ReadEpisodeDate(root, "last_episode_to_air"),
                NextAirDate = ReadEpisodeDate(root, "next_episode_to_air"),
                FetchedUtc = DateTime.UtcNow,
            };
        }

        /// <summary>Find a TMDB tv id from a TVDB or IMDb id. source is "tvdb_id" or "imdb_id".</summary>
        public async Task<string> FindSeriesIdAsync(string externalId, string source, CancellationToken ct)
        {
            using var doc = await this.GetAsync("find/" + Uri.EscapeDataString(externalId) + "?external_source=" + source, ct).ConfigureAwait(false);
            if (doc != null
                && doc.RootElement.TryGetProperty("tv_results", out var results)
                && results.ValueKind == JsonValueKind.Array
                && results.GetArrayLength() > 0
                && results[0].TryGetProperty("id", out var id))
            {
                return id.GetRawText();
            }

            return null;
        }

        private static DateTime? ReadEpisodeDate(JsonElement root, string property)
        {
            if (root.TryGetProperty(property, out var ep)
                && ep.ValueKind == JsonValueKind.Object
                && ep.TryGetProperty("air_date", out var d)
                && d.ValueKind == JsonValueKind.String
                && DateTime.TryParseExact(d.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            {
                return date;
            }

            return null;
        }

        private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
        {
            var url = BaseUrl + path;
            if (!this.UsesBearer)
            {
                url += (url.Contains('?') ? "&" : "?") + "api_key=" + Uri.EscapeDataString(this.key);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (this.UsesBearer)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", this.key);
            }

            for (var attempt = 0; ; attempt++)
            {
                using var response = await Http.Client.SendAsync(request.Clone(), ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                if (response.StatusCode == (HttpStatusCode)429 && attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct).ConfigureAwait(false);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new InvalidOperationException("TMDB rejected the API key (401). Check it in the plugin settings.");
                }

                response.EnsureSuccessStatusCode();
                var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Minimal MDBList client: one title's IMDb and Rotten Tomatoes ratings.</summary>
    public sealed class MdbListClient
    {
        private const string BaseUrl = "https://api.mdblist.com/";
        private readonly string key;

        public MdbListClient(string key)
        {
            this.key = (key ?? string.Empty).Trim();
        }

        public bool IsConfigured => this.key.Length > 0;

        /// <summary>The account's daily request limit and how many it has used today (shared by every tool using this key).</summary>
        public async Task<MdbListQuota> GetQuotaAsync(CancellationToken ct)
        {
            var url = $"{BaseUrl}user?apikey={Uri.EscapeDataString(this.key)}";
            using var response = await Http.Client.GetAsync(url, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new InvalidOperationException($"MDBList rejected the API key ({(int)response.StatusCode}). Check it in the plugin settings.");
            }

            response.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            var limit = ReadNumber(doc.RootElement, "api_requests");
            var used = ReadNumber(doc.RootElement, "api_requests_count");
            if (limit == null)
            {
                return null;
            }

            return new MdbListQuota
            {
                DailyLimit = (int)limit.Value,
                UsedToday = (int)(used ?? 0),
                CheckedUtc = DateTime.UtcNow,
            };
        }

        /// <param name="provider">"imdb" or "tmdb".</param>
        /// <param name="mediaType">"movie" or "show".</param>
        public async Task<RatingsCacheEntry> GetRatingsAsync(string provider, string mediaType, string id, CancellationToken ct)
        {
            var url = $"{BaseUrl}{provider}/{mediaType}/{Uri.EscapeDataString(id)}/?apikey={Uri.EscapeDataString(this.key)}";
            using var response = await Http.Client.GetAsync(url, ct).ConfigureAwait(false);

            // 404, and 400 for ids MDBList can't use (seen in the wild for some TV ids): record as "not on MDBList"
            // so the title waits for its normal refresh interval instead of being retried every run.
            if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.BadRequest)
            {
                return new RatingsCacheEntry { NotFound = true, FetchedUtc = DateTime.UtcNow };
            }

            if (response.StatusCode == (HttpStatusCode)429)
            {
                TimeSpan? retryAfter = null;
                var header = response.Headers.RetryAfter;
                if (header?.Delta != null)
                {
                    retryAfter = header.Delta;
                }
                else if (header?.Date != null)
                {
                    retryAfter = header.Date.Value - DateTimeOffset.UtcNow;
                }

                string detail = null;
                try
                {
                    detail = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))?.Trim();
                }
                catch
                {
                    // The body is only for the log.
                }

                if (!string.IsNullOrEmpty(detail) && detail.Length > 200)
                {
                    detail = detail.Substring(0, 200);
                }

                throw new RateLimitedException(
                    "MDBList said too many requests (HTTP 429)" + (string.IsNullOrEmpty(detail) ? string.Empty : ": " + detail),
                    retryAfter);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new InvalidOperationException($"MDBList rejected the API key ({(int)response.StatusCode}). Check it in the plugin settings.");
            }

            response.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            var entry = new RatingsCacheEntry { FetchedUtc = DateTime.UtcNow };

            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("ratings", out var ratings)
                && ratings.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in ratings.EnumerateArray())
                {
                    var source = r.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                    var value = ReadNumber(r, "value");
                    if (source == null || value == null || value <= 0)
                    {
                        continue;
                    }

                    switch (source)
                    {
                        case "imdb":
                            if (value <= 10) entry.Imdb = Math.Round(value.Value, 1);
                            break;
                        case "tomatoes":
                            if (value <= 100) entry.RtCritic = (int)Math.Round(value.Value);
                            break;
                        case "tomatoesaudience":
                        case "popcorn":
                            if (value <= 100) entry.RtAudience = (int)Math.Round(value.Value);
                            break;
                    }
                }
            }
            else if (doc.RootElement.TryGetProperty("error", out _))
            {
                entry.NotFound = true;
            }

            return entry;
        }

        private static double? ReadNumber(JsonElement e, string property)
        {
            if (!e.TryGetProperty(property, out var v))
            {
                return null;
            }

            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
            {
                return d;
            }

            if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            {
                return d;
            }

            return null;
        }
    }

    internal static class HttpRequestMessageExtensions
    {
        public static HttpRequestMessage Clone(this HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return clone;
        }
    }
}
