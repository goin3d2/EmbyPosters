using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Model.Logging;

namespace PosterOverlays.Data
{
    /// <summary>Series broadcast status, normalized from TMDB or Emby.</summary>
    public enum ShowStatus
    {
        None,
        Airing,
        Returning,
        Ended,
        Canceled,
        Upcoming,
    }

    /// <summary>What gets drawn on one item's poster. Filled in by the refresh task; read by the enhancer.</summary>
    public class ItemOverlayData
    {
        public ShowStatus Status { get; set; }

        /// <summary>res_4k, res_1080p, res_720p, res_576p, res_480p, or null.</summary>
        public string Resolution { get; set; }

        /// <summary>hdr_dv, hdr_hdr10plus, hdr_hdr10, hdr_hlg, or null.</summary>
        public string Hdr { get; set; }

        /// <summary>audio_truehd_atmos, audio_plus_atmos, audio_atmos, audio_dtsx, audio_dtshdma, audio_truehd, audio_ddplus, audio_dts, audio_dd, or null.</summary>
        public string Audio { get; set; }

        public double? Imdb { get; set; }

        public int? RtCritic { get; set; }

        public int? RtAudience { get; set; }

        /// <summary>TMDB score as stored by Emby (its community rating).</summary>
        public double? Tmdb { get; set; }
    }

    public class ImdbRating
    {
        public double Rating { get; set; }

        public int Votes { get; set; }
    }

    /// <summary>TMDB lookup result cached per TMDB series id.</summary>
    public class StatusCacheEntry
    {
        public string TmdbStatus { get; set; }

        public DateTime? LastAirDate { get; set; }

        public DateTime? NextAirDate { get; set; }

        public DateTime FetchedUtc { get; set; }
    }

    /// <summary>MDBList result cached per "type/provider/id".</summary>
    public class RatingsCacheEntry
    {
        public double? Imdb { get; set; }

        public int? RtCritic { get; set; }

        public int? RtAudience { get; set; }

        public bool NotFound { get; set; }

        public DateTime FetchedUtc { get; set; }
    }

    public class OverlayStoreData
    {
        public int Version { get; set; } = 1;

        /// <summary>Keyed by Emby internal item id.</summary>
        public ConcurrentDictionary<long, ItemOverlayData> Items { get; set; } = new ConcurrentDictionary<long, ItemOverlayData>();

        /// <summary>Keyed by TMDB series id.</summary>
        public ConcurrentDictionary<string, StatusCacheEntry> Status { get; set; } = new ConcurrentDictionary<string, StatusCacheEntry>();

        /// <summary>Keyed by "movie:imdb:tt123" style keys.</summary>
        public ConcurrentDictionary<string, RatingsCacheEntry> Ratings { get; set; } = new ConcurrentDictionary<string, RatingsCacheEntry>();

        /// <summary>TVDB/IMDb id to TMDB id lookups, keyed by "tvdb:123" / "imdb:tt123".</summary>
        public ConcurrentDictionary<string, string> TmdbIdLookups { get; set; } = new ConcurrentDictionary<string, string>();

        /// <summary>IMDb ratings from IMDb's daily dataset, for titles in the library, keyed by tt id.</summary>
        public ConcurrentDictionary<string, ImdbRating> ImdbRatings { get; set; } = new ConcurrentDictionary<string, ImdbRating>();

        public DateTime? ImdbDatasetFetchedUtc { get; set; }

        /// <summary>Last MDBList quota reading, shown on the settings page.</summary>
        public PosterOverlays.Services.MdbListQuota MdbListQuota { get; set; }
    }

    /// <summary>Small JSON-backed store. Reads are lock-free; saves write a temp file then swap.</summary>
    public class OverlayStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
        };

        private readonly string path;
        private readonly ILogger logger;
        private readonly object saveLock = new object();

        private OverlayStore(string path, ILogger logger, OverlayStoreData data)
        {
            this.path = path;
            this.logger = logger;
            this.Data = data;
        }

        public OverlayStoreData Data { get; }

        public static OverlayStore Load(string path, ILogger logger)
        {
            OverlayStoreData data = null;
            try
            {
                if (File.Exists(path))
                {
                    data = JsonSerializer.Deserialize<OverlayStoreData>(File.ReadAllText(path), JsonOptions);
                }
            }
            catch (Exception ex)
            {
                logger.ErrorException("Could not read {0}; starting with an empty cache", ex, path);
            }

            return new OverlayStore(path, logger, data ?? new OverlayStoreData());
        }

        public ItemOverlayData GetItem(long internalId)
        {
            return this.Data.Items.TryGetValue(internalId, out var value) ? value : null;
        }

        public void SetItem(long internalId, ItemOverlayData value)
        {
            this.Data.Items[internalId] = value;
        }

        public void Save()
        {
            lock (this.saveLock)
            {
                try
                {
                    var tmp = this.path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(this.Data, JsonOptions));
                    File.Move(tmp, this.path, true);
                }
                catch (Exception ex)
                {
                    this.logger.ErrorException("Could not save {0}", ex, this.path);
                }
            }
        }
    }
}
