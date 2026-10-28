using System;
using System.Collections.Generic;
using System.Linq;

namespace PosterOverlays
{
    public enum BannerPosition
    {
        Bottom,
        Top,
    }

    public enum ImdbRatingSource
    {
        /// <summary>IMDb's free daily ratings file: no API calls.</summary>
        ImdbDataset,

        /// <summary>MDBList, one call per title (uses quota).</summary>
        MdbList,
    }

    public enum SeriesQualityRule
    {
        LatestSeason,
        BestAvailable,
        MostEpisodes,
    }

    /// <summary>
    /// All plugin settings. Stored as settings.json in the plugin's data folder and edited on the plugin's own
    /// settings page (Configuration/configPage.html), which also renders live previews.
    /// </summary>
    public class PluginOptions
    {
        public const string DefaultIconSource =
            "https://raw.githubusercontent.com/Kometa-Team/Kometa/e1327dc4e7f709277e3e2be88df76c3eefe349e2/defaults/overlays/images";

        public const string DefaultBadgeOrder = "resolution, hdr, audio, imdb, rtcritic, rtaudience";

        public static readonly string[] BadgeKeys = { "resolution", "hdr", "audio", "imdb", "rtcritic", "rtaudience", "tmdb" };

        // Where
        public bool EnableMovies { get; set; } = true;

        public bool EnableSeries { get; set; } = true;

        public bool EnableSeasons { get; set; } = true;

        // Status banner
        public bool EnableStatusBanner { get; set; } = true;

        public BannerPosition BannerPosition { get; set; } = BannerPosition.Bottom;

        public int BannerHeightPercent { get; set; } = 9;

        public int AiringWindowDays { get; set; } = 15;

        public bool ShowUpcoming { get; set; } = true;

        public string BannerTextColor { get; set; } = "#FFFFFF";

        public string AiringText { get; set; } = "AIRING";

        public string AiringColor { get; set; } = "#1D9E75";

        public string ReturningText { get; set; } = "RETURNING";

        public string ReturningColor { get; set; } = "#378ADD";

        public string EndedText { get; set; } = "ENDED";

        public string EndedColor { get; set; } = "#5F5E5A";

        public string CanceledText { get; set; } = "CANCELED";

        public string CanceledColor { get; set; } = "#C0392B";

        public string UpcomingText { get; set; } = "UPCOMING";

        public string UpcomingColor { get; set; } = "#7F77DD";

        // Badges
        public bool ShowResolution { get; set; } = true;

        public bool ShowHdr { get; set; } = true;

        public bool ShowAudio { get; set; } = true;

        public bool ShowBasicAudio { get; set; } = false;

        public bool ShowImdb { get; set; } = true;

        public bool ShowRtCritic { get; set; } = true;

        public bool ShowRtAudience { get; set; } = false;

        public bool ShowTmdb { get; set; } = false;

        public bool ShowRatingsOnSeasons { get; set; } = false;

        public string BadgeOrder { get; set; } = DefaultBadgeOrder;

        public int BadgeHeightPercent { get; set; } = 5;

        public int BadgeMarginPercent { get; set; } = 3;

        public int MaxBadges { get; set; } = 6;

        public string BadgeBackgroundColor { get; set; } = "#000000";

        public int BadgeBackgroundOpacity { get; set; } = 70;

        public SeriesQualityRule SeriesQualityRule { get; set; } = SeriesQualityRule.LatestSeason;

        // Icons
        public bool UseIcons { get; set; } = true;

        public string IconSourceBaseUrl { get; set; } = DefaultIconSource;

        // Data sources
        public string TmdbApiKey { get; set; } = string.Empty;

        public string MdbListApiKey { get; set; } = string.Empty;

        public ImdbRatingSource ImdbSource { get; set; } = ImdbRatingSource.ImdbDataset;

        /// <summary>Extra cap per run; 0 means only the account's remaining daily quota limits a run.</summary>
        public int MaxMdbListCallsPerRun { get; set; } = 0;

        /// <summary>Calls left untouched each day, for other tools using the same MDBList key.</summary>
        public int MdbListReserve { get; set; } = 100;

        public int RecentRatingsRefreshDays { get; set; } = 3;

        public int OldRatingsRefreshDays { get; set; } = 60;

        public IReadOnlyList<string> GetBadgeOrder()
        {
            var parsed = (this.BadgeOrder ?? string.Empty)
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().ToLowerInvariant())
                .Where(BadgeKeys.Contains)
                .Distinct()
                .ToList();

            // Anything left out is appended so a typo never hides a badge type entirely.
            parsed.AddRange(BadgeKeys.Where(k => !parsed.Contains(k)));
            return parsed;
        }

        /// <summary>Clamp numbers, fix colors and fill blanks so a bad value from the page can't break rendering.</summary>
        public PluginOptions Normalize()
        {
            var d = new PluginOptions();
            this.BannerHeightPercent = Clamp(this.BannerHeightPercent, 4, 20);
            this.AiringWindowDays = Clamp(this.AiringWindowDays, 1, 60);
            this.BadgeHeightPercent = Clamp(this.BadgeHeightPercent, 3, 12);
            this.BadgeMarginPercent = Clamp(this.BadgeMarginPercent, 0, 10);
            this.MaxBadges = Clamp(this.MaxBadges, 1, 10);
            this.BadgeBackgroundOpacity = Clamp(this.BadgeBackgroundOpacity, 0, 100);
            this.MaxMdbListCallsPerRun = Clamp(this.MaxMdbListCallsPerRun, 0, 1000000);
            this.MdbListReserve = Clamp(this.MdbListReserve, 0, 1000000);
            this.RecentRatingsRefreshDays = Clamp(this.RecentRatingsRefreshDays, 1, 90);
            this.OldRatingsRefreshDays = Clamp(this.OldRatingsRefreshDays, 1, 365);

            this.BannerTextColor = Color(this.BannerTextColor, d.BannerTextColor);
            this.AiringColor = Color(this.AiringColor, d.AiringColor);
            this.ReturningColor = Color(this.ReturningColor, d.ReturningColor);
            this.EndedColor = Color(this.EndedColor, d.EndedColor);
            this.CanceledColor = Color(this.CanceledColor, d.CanceledColor);
            this.UpcomingColor = Color(this.UpcomingColor, d.UpcomingColor);
            this.BadgeBackgroundColor = Color(this.BadgeBackgroundColor, d.BadgeBackgroundColor);

            this.AiringText = (this.AiringText ?? string.Empty).Trim();
            this.ReturningText = (this.ReturningText ?? string.Empty).Trim();
            this.EndedText = (this.EndedText ?? string.Empty).Trim();
            this.CanceledText = (this.CanceledText ?? string.Empty).Trim();
            this.UpcomingText = (this.UpcomingText ?? string.Empty).Trim();
            this.BadgeOrder = string.Join(", ", this.GetBadgeOrder());
            this.IconSourceBaseUrl = string.IsNullOrWhiteSpace(this.IconSourceBaseUrl) ? d.IconSourceBaseUrl : this.IconSourceBaseUrl.Trim();
            this.TmdbApiKey = (this.TmdbApiKey ?? string.Empty).Trim();
            this.MdbListApiKey = (this.MdbListApiKey ?? string.Empty).Trim();
            return this;
        }

        /// <summary>Every option that affects the rendered image (not data sources).</summary>
        public object GetStyleSnapshot() => new
        {
            this.EnableMovies, this.EnableSeries, this.EnableSeasons,
            this.EnableStatusBanner, this.BannerPosition, this.BannerHeightPercent, this.ShowUpcoming, this.BannerTextColor,
            this.AiringText, this.AiringColor, this.ReturningText, this.ReturningColor, this.EndedText, this.EndedColor,
            this.CanceledText, this.CanceledColor, this.UpcomingText, this.UpcomingColor,
            this.ShowResolution, this.ShowHdr, this.ShowAudio, this.ShowBasicAudio, this.ShowImdb, this.ShowRtCritic,
            this.ShowRtAudience, this.ShowTmdb, this.ShowRatingsOnSeasons, Order = string.Join(",", this.GetBadgeOrder()),
            this.BadgeHeightPercent, this.BadgeMarginPercent, this.MaxBadges, this.BadgeBackgroundColor, this.BadgeBackgroundOpacity,
            this.UseIcons,
        };

        private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));

        private static string Color(string value, string fallback) =>
            Drawing.Colors.TryParse(value, out var c) ? $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}" : fallback;
    }
}
