using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PosterOverlays.Data;

namespace PosterOverlays.Services
{
    /// <summary>
    /// IMDb's own ratings file (title.ratings.tsv.gz, about 8 MB, refreshed daily by IMDb, free for personal and
    /// non-commercial use). One download covers every title, so the IMDb badge needs no per-title API calls.
    /// Only ratings for titles in the library are kept.
    /// </summary>
    public static class ImdbDataset
    {
        public const string Url = "https://datasets.imdbws.com/title.ratings.tsv.gz";

        private static readonly TimeSpan MaxAge = TimeSpan.FromHours(20);

        /// <summary>Download the file if the local copy is missing or older than a day, then load the wanted titles.</summary>
        /// <returns>Number of wanted titles found, and whether a download happened.</returns>
        public static async Task<(int Found, bool Downloaded)> RefreshAsync(string localPath, ISet<string> wanted, OverlayStore store, CancellationToken ct)
        {
            var downloaded = false;
            var fresh = File.Exists(localPath)
                && store.Data.ImdbDatasetFetchedUtc.HasValue
                && DateTime.UtcNow - store.Data.ImdbDatasetFetchedUtc.Value < MaxAge;

            if (!fresh)
            {
                var tmp = localPath + ".tmp";
                using (var response = await Http.Client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    using var file = File.Create(tmp);
                    await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
                }

                File.Move(tmp, localPath, true);
                store.Data.ImdbDatasetFetchedUtc = DateTime.UtcNow;
                downloaded = true;
            }

            using var stream = File.OpenRead(localPath);
            var found = Load(stream, wanted, store.Data.ImdbRatings);
            return (found, downloaded);
        }

        /// <summary>Read a gzipped title.ratings TSV and put the wanted titles into target. Returns how many were found.</summary>
        public static int Load(Stream gzipped, ISet<string> wanted, IDictionary<string, ImdbRating> target)
        {
            var found = 0;
            using var gz = new GZipStream(gzipped, CompressionMode.Decompress);
            using var reader = new StreamReader(gz);

            // Header: tconst  averageRating  numVotes
            var line = reader.ReadLine();
            while ((line = reader.ReadLine()) != null)
            {
                var tab = line.IndexOf('\t');
                if (tab <= 0)
                {
                    continue;
                }

                var id = line.Substring(0, tab);
                if (!wanted.Contains(id))
                {
                    continue;
                }

                var parts = line.Split('\t');
                if (parts.Length >= 3
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var rating)
                    && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var votes)
                    && rating > 0 && rating <= 10)
                {
                    target[id] = new ImdbRating { Rating = Math.Round(rating, 1), Votes = votes };
                    found++;
                }
            }

            return found;
        }
    }
}
