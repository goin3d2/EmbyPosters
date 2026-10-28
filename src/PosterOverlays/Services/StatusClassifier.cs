using System;
using MediaBrowser.Model.Entities;
using PosterOverlays.Data;

namespace PosterOverlays.Services
{
    public static class StatusClassifier
    {
        /// <summary>Map a TMDB status (plus air dates) to a banner status. Same mapping Kometa and Agregarr use.</summary>
        public static ShowStatus FromTmdb(StatusCacheEntry entry, int airingWindowDays, DateTime todayUtc)
        {
            if (entry == null || string.IsNullOrEmpty(entry.TmdbStatus))
            {
                return ShowStatus.None;
            }

            switch (entry.TmdbStatus.Trim().ToLowerInvariant())
            {
                case "returning series":
                    var window = TimeSpan.FromDays(airingWindowDays);
                    var today = todayUtc.Date;
                    var recentlyAired = entry.LastAirDate.HasValue && entry.LastAirDate.Value.Date <= today && today - entry.LastAirDate.Value.Date <= window;
                    var airsSoon = entry.NextAirDate.HasValue && entry.NextAirDate.Value.Date >= today && entry.NextAirDate.Value.Date - today <= window;
                    return recentlyAired || airsSoon ? ShowStatus.Airing : ShowStatus.Returning;
                case "ended":
                    return ShowStatus.Ended;
                case "canceled":
                case "cancelled":
                    return ShowStatus.Canceled;
                case "planned":
                case "in production":
                case "pilot":
                    return ShowStatus.Upcoming;
                default:
                    return ShowStatus.None;
            }
        }

        /// <summary>Fallback when there's no TMDB key: Emby only knows Continuing or Ended.</summary>
        public static ShowStatus FromEmby(SeriesStatus? status)
        {
            switch (status)
            {
                case SeriesStatus.Continuing:
                    return ShowStatus.Returning;
                case SeriesStatus.Ended:
                    return ShowStatus.Ended;
                default:
                    return ShowStatus.None;
            }
        }
    }
}
