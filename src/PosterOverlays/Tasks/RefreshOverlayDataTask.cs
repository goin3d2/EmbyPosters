using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using PosterOverlays.Data;
using PosterOverlays.Services;

namespace PosterOverlays.Tasks
{
    /// <summary>
    /// Gathers everything the enhancer draws: series status (TMDB), ratings (MDBList) and quality (media streams).
    /// API results are cached, so later runs only call out for stale entries.
    /// </summary>
    public class RefreshOverlayDataTask : IScheduledTask
    {
        private const int EpisodeSampleLimit = 60;

        private readonly ILibraryManager libraryManager;
        private readonly ILogger logger;

        public RefreshOverlayDataTask(ILibraryManager libraryManager, ILogManager logManager)
        {
            this.libraryManager = libraryManager;
            this.logger = logManager.GetLogger(Plugin.PluginName);
        }

        public string Name => "Refresh poster overlay data";

        public string Key => "PosterOverlaysRefreshData";

        public string Description => "Updates series status, ratings and quality badges used by Poster Overlays.";

        public string Category => Plugin.PluginName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerInterval, IntervalTicks = TimeSpan.FromHours(6).Ticks },
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("Poster Overlays plugin is not loaded");
            var options = plugin.Options;
            var store = plugin.Store;
            var tmdb = new TmdbClient(options.TmdbApiKey);
            var mdb = new MdbListClient(options.MdbListApiKey);
            var run = new RunState();
            var mostCommon = options.SeriesQualityRule == SeriesQualityRule.MostEpisodes;

            if (!tmdb.IsConfigured)
            {
                this.logger.Info("No TMDB key set: series status comes from Emby (Returning/Ended only)");
            }

            run.NeedMdbList = NeedsMdbList(options);
            if (run.NeedMdbList && !mdb.IsConfigured)
            {
                this.logger.Info("No MDBList key set: Rotten Tomatoes badges are skipped");
            }
            else if (run.NeedMdbList)
            {
                run.MdbBudget = await this.GetMdbBudgetAsync(mdb, store, options, cancellationToken).ConfigureAwait(false);
            }

            var movies = options.EnableMovies ? this.Query("Movie") : Array.Empty<BaseItem>();
            var series = options.EnableSeries || options.EnableSeasons ? this.Query("Series").OfType<Series>().ToArray() : Array.Empty<Series>();
            var total = Math.Max(1, movies.Length + series.Length);
            var done = 0;
            var seen = new HashSet<long>();

            if (options.ShowImdb && options.ImdbSource == ImdbRatingSource.ImdbDataset)
            {
                await this.RefreshImdbDatasetAsync(movies, series, plugin.DataDirectory, store, cancellationToken).ConfigureAwait(false);
            }

            // Ratings first, in priority order and within the day's budget; the loops below only read the cache.
            // Whatever happens here, the posters still get updated with everything fetched so far.
            try
            {
                await this.FetchRatingsAsync(movies, series, mdb, store, run, options, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                run.MdbStopped = true;
                store.Save();
                this.logger.ErrorException("Fetching ratings stopped early; updating posters with what was fetched", ex);
            }

            // Emby keeps each version of a movie (1080p, 4K...) as its own item and shows them as one title.
            // Every version gets the best quality found across the set, so the card shows 4K whichever version Emby picks.
            foreach (var versions in movies.GroupBy(VersionGroupKey))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var perVersion = new List<QualityInfo>();
                foreach (var movie in versions)
                {
                    try
                    {
                        perVersion.Add(MediaInfoClassifier.Classify(movie.GetMediaStreams(cancellationToken), movie.Path));
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        this.logger.ErrorException("Could not read media info for movie {0}", ex, movie.Name);
                    }
                }

                var quality = MediaInfoClassifier.Summarize(perVersion, mostCommon: false);
                foreach (var movie in versions)
                {
                    var data = new ItemOverlayData { Resolution = quality.Resolution, Hdr = quality.Hdr, Audio = quality.Audio };
                    ApplyRatings(data, movie, "movie", store, options);
                    store.SetItem(movie.InternalId, data);
                    seen.Add(movie.InternalId);
                    progress.Report(40 + (60.0 * ++done / total));
                    if (done % 250 == 0)
                    {
                        store.Save();
                    }
                }
            }

            foreach (var show in series)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await this.RefreshSeriesAsync(show, tmdb, mdb, store, run, options, mostCommon, seen, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    this.logger.ErrorException("Poster overlay data failed for series {0}", ex, show.Name);
                }

                progress.Report(40 + (60.0 * ++done / total));
                if (done % 50 == 0)
                {
                    store.Save();
                }
            }

            // Forget items that no longer exist (or whose type was switched off).
            foreach (var stale in store.Data.Items.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                store.Data.Items.TryRemove(stale, out _);
            }

            if (store.Data.MdbListQuota != null && run.MdbCalls > 0)
            {
                // Keep the shown usage roughly current until the next check.
                store.Data.MdbListQuota.UsedToday += run.MdbCalls;
            }

            store.Save();
            this.logger.Info(
                "Poster overlay data refreshed: {0} movies, {1} series; {2} TMDB calls; MDBList: {3} titles updated in {4} calls, {5} still waiting{6}",
                movies.Length,
                series.Length,
                run.TmdbCalls,
                run.MdbFetched,
                run.MdbCalls,
                run.MdbSkipped,
                run.MdbStopped ? " (stopped early; the rest continue next run)" : string.Empty);
        }

        /// <summary>
        /// How many MDBList calls this run may make: what the account has left today, minus the reserve, capped by
        /// the per-run limit (0 = no per-run limit). Falls back to the per-run limit if the quota can't be read.
        /// </summary>
        private async Task<int> GetMdbBudgetAsync(MdbListClient mdb, OverlayStore store, PluginOptions options, CancellationToken ct)
        {
            var perRunCap = options.MaxMdbListCallsPerRun > 0 ? options.MaxMdbListCallsPerRun : int.MaxValue;
            try
            {
                var quota = await mdb.GetQuotaAsync(ct).ConfigureAwait(false);
                if (quota != null)
                {
                    store.Data.MdbListQuota = quota;
                    var budget = Math.Min(perRunCap, Math.Max(0, quota.Remaining - options.MdbListReserve));
                    this.logger.Info(
                        "MDBList quota: {0} of {1} used today, keeping {2} in reserve; this run may use {3}",
                        quota.UsedToday,
                        quota.DailyLimit,
                        options.MdbListReserve,
                        budget);
                    return budget;
                }
            }
            catch (InvalidOperationException ex)
            {
                this.logger.Error(ex.Message);
                return 0;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                this.logger.Warn("Could not read the MDBList quota ({0}); using the per-run limit", ex.Message);
            }

            return options.MaxMdbListCallsPerRun > 0 ? options.MaxMdbListCallsPerRun : 900;
        }

        private async Task RefreshSeriesAsync(
            Series show,
            TmdbClient tmdb,
            MdbListClient mdb,
            OverlayStore store,
            RunState run,
            PluginOptions options,
            bool mostCommon,
            HashSet<long> seen,
            CancellationToken ct)
        {
            var status = await this.GetStatusAsync(show, tmdb, store, run, options, ct).ConfigureAwait(false);
            var ratings = new ItemOverlayData();
            ApplyRatings(ratings, show, "show", store, options);

            var episodes = this.Query("Episode", show);
            var perSeason = episodes
                .GroupBy(e => e.ParentIndexNumber ?? -1)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.IndexNumber ?? 0).Take(EpisodeSampleLimit).ToList());

            var qualityCache = new Dictionary<long, QualityInfo>();
            QualityInfo QualityOf(BaseItem e)
            {
                if (!qualityCache.TryGetValue(e.InternalId, out var q))
                {
                    q = MediaInfoClassifier.Classify(e.GetMediaStreams(ct), e.Path);
                    qualityCache[e.InternalId] = q;
                }

                return q;
            }

            if (options.EnableSeries)
            {
                IEnumerable<BaseItem> sample;
                if (options.SeriesQualityRule == SeriesQualityRule.LatestSeason)
                {
                    // Latest real season that has files (season 0 / specials only as a last resort).
                    var latest = perSeason.Keys.Where(k => k > 0).DefaultIfEmpty(perSeason.Keys.DefaultIfEmpty(-1).Max()).Max();
                    sample = perSeason.TryGetValue(latest, out var eps) ? eps : Enumerable.Empty<BaseItem>();
                }
                else
                {
                    sample = perSeason.Values.SelectMany(v => v).OrderByDescending(e => e.PremiereDate ?? DateTimeOffset.MinValue).Take(EpisodeSampleLimit * 2);
                }

                var quality = MediaInfoClassifier.Summarize(PerEpisode(sample, QualityOf), mostCommon);
                store.SetItem(show.InternalId, new ItemOverlayData
                {
                    Status = status,
                    Resolution = quality.Resolution,
                    Hdr = quality.Hdr,
                    Audio = quality.Audio,
                    Imdb = ratings.Imdb,
                    RtCritic = ratings.RtCritic,
                    RtAudience = ratings.RtAudience,
                    Tmdb = ratings.Tmdb,
                });
                seen.Add(show.InternalId);
            }

            if (options.EnableSeasons)
            {
                foreach (var season in this.Query("Season", show).OfType<Season>())
                {
                    var eps = season.IndexNumber.HasValue && perSeason.TryGetValue(season.IndexNumber.Value, out var list) ? list : new List<BaseItem>();
                    var quality = MediaInfoClassifier.Summarize(PerEpisode(eps, QualityOf), mostCommon);
                    store.SetItem(season.InternalId, new ItemOverlayData
                    {
                        Status = status,
                        Resolution = quality.Resolution,
                        Hdr = quality.Hdr,
                        Audio = quality.Audio,
                        Imdb = ratings.Imdb,
                        RtCritic = ratings.RtCritic,
                        RtAudience = ratings.RtAudience,
                        Tmdb = ratings.Tmdb,
                    });
                    seen.Add(season.InternalId);
                }
            }
        }

        private async Task<ShowStatus> GetStatusAsync(Series show, TmdbClient tmdb, OverlayStore store, RunState run, PluginOptions options, CancellationToken ct)
        {
            var fallback = StatusClassifier.FromEmby(show.Status);
            if (!tmdb.IsConfigured || run.TmdbStopped)
            {
                return fallback;
            }

            var tmdbId = await this.ResolveTmdbIdAsync(show, tmdb, store, run, ct).ConfigureAwait(false);
            if (tmdbId == null)
            {
                return fallback;
            }

            store.Data.Status.TryGetValue(tmdbId, out var entry);
            if (entry == null || IsStale(entry))
            {
                try
                {
                    run.TmdbCalls++;
                    var fresh = await tmdb.GetSeriesStatusAsync(tmdbId, ct).ConfigureAwait(false);
                    if (fresh != null)
                    {
                        entry = fresh;
                        store.Data.Status[tmdbId] = entry;
                    }

                    await Task.Delay(40, ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    run.TmdbStopped = true;
                    this.logger.Error(ex.Message);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    this.logger.Warn("TMDB status lookup failed for {0} ({1}): {2}", show.Name, tmdbId, ex.Message);
                }
            }

            var mapped = StatusClassifier.FromTmdb(entry, options.AiringWindowDays, DateTime.UtcNow);
            return mapped == ShowStatus.None ? fallback : mapped;
        }

        private static bool IsStale(StatusCacheEntry entry)
        {
            var age = DateTime.UtcNow - entry.FetchedUtc;
            var settled = string.Equals(entry.TmdbStatus, "Ended", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.TmdbStatus, "Canceled", StringComparison.OrdinalIgnoreCase);

            // Running shows: daily, so Airing flips on time. Finished shows rarely change.
            return age > (settled ? TimeSpan.FromDays(14) : TimeSpan.FromHours(20));
        }

        private async Task<string> ResolveTmdbIdAsync(Series show, TmdbClient tmdb, OverlayStore store, RunState run, CancellationToken ct)
        {
            var direct = show.GetProviderId(MetadataProviders.Tmdb);
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct.Trim();
            }

            foreach (var (provider, source) in new[] { (MetadataProviders.Tvdb, "tvdb_id"), (MetadataProviders.Imdb, "imdb_id") })
            {
                var external = show.GetProviderId(provider);
                if (string.IsNullOrWhiteSpace(external))
                {
                    continue;
                }

                var lookupKey = source.Substring(0, 4) + ":" + external.Trim();
                if (store.Data.TmdbIdLookups.TryGetValue(lookupKey, out var cached))
                {
                    return cached.Length == 0 ? null : cached;
                }

                try
                {
                    run.TmdbCalls++;
                    var found = await tmdb.FindSeriesIdAsync(external.Trim(), source, ct).ConfigureAwait(false);
                    store.Data.TmdbIdLookups[lookupKey] = found ?? string.Empty;
                    if (found != null)
                    {
                        return found;
                    }
                }
                catch (InvalidOperationException ex)
                {
                    run.TmdbStopped = true;
                    this.logger.Error(ex.Message);
                    return null;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    this.logger.Warn("TMDB id lookup failed for {0}: {1}", show.Name, ex.Message);
                }
            }

            return null;
        }

        /// <summary>The MDBList lookup for one title: cache key plus what to ask for.</summary>
        private static (string Key, string Provider, string Id) RatingsKey(BaseItem item, string mediaType)
        {
            string provider = null, id = item.GetProviderId(MetadataProviders.Imdb);
            if (!string.IsNullOrWhiteSpace(id))
            {
                provider = "imdb";
            }
            else
            {
                id = item.GetProviderId(MetadataProviders.Tmdb);
                provider = string.IsNullOrWhiteSpace(id) ? null : "tmdb";
            }

            if (provider == null)
            {
                return (null, null, null);
            }

            id = id.Trim();
            return (mediaType + ":" + provider + ":" + id, provider, id);
        }

        /// <summary>
        /// Fetch ratings within the budget, most useful first: titles that have none yet (newest first, movies and
        /// series mixed), then refreshes of stale ones (oldest fetch first). Anything left over waits for the next run.
        /// </summary>
        private async Task FetchRatingsAsync(
            IReadOnlyList<BaseItem> movies,
            IReadOnlyList<Series> series,
            MdbListClient mdb,
            OverlayStore store,
            RunState run,
            PluginOptions options,
            IProgress<double> progress,
            CancellationToken ct)
        {
            if (!mdb.IsConfigured || !run.NeedMdbList)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var needed = new Dictionary<string, (string Provider, string MediaType, string Id, string Name, bool Missing, DateTimeOffset Premiere, DateTime Fetched)>();
            void Consider(BaseItem item, string mediaType)
            {
                var (key, provider, id) = RatingsKey(item, mediaType);
                if (key == null || needed.ContainsKey(key))
                {
                    return;
                }

                store.Data.Ratings.TryGetValue(key, out var entry);
                var recent = item.PremiereDate.HasValue && item.PremiereDate.Value > DateTimeOffset.UtcNow.AddYears(-1);
                var maxAge = TimeSpan.FromDays(recent ? options.RecentRatingsRefreshDays : options.OldRatingsRefreshDays);
                if (entry == null || now - entry.FetchedUtc > maxAge)
                {
                    needed[key] = (provider, mediaType, id, item.Name, entry == null, item.PremiereDate ?? DateTimeOffset.MinValue, entry?.FetchedUtc ?? DateTime.MinValue);
                }
            }

            foreach (var m in movies)
            {
                Consider(m, "movie");
            }

            foreach (var s in series)
            {
                Consider(s, "show");
            }

            var queue = needed
                .OrderByDescending(p => p.Value.Missing)
                .ThenByDescending(p => p.Value.Missing ? p.Value.Premiere : DateTimeOffset.MinValue)
                .ThenBy(p => p.Value.Fetched)
                .ToList();

            var limit = Math.Min(queue.Count, run.MdbBudget);
            this.logger.Info("MDBList: {0} titles need ratings; fetching up to {1} this run", queue.Count, limit);
            var pace = BasePace;
            var limitedInARow = 0;
            var failedInARow = 0;
            var lastSavedIndex = 0;
            var lastReported = 0;
            var lastSaved = DateTime.UtcNow;
            var index = 0;
            while (index < queue.Count)
            {
                if (run.MdbStopped || run.MdbCalls >= run.MdbBudget)
                {
                    break;
                }

                ct.ThrowIfCancellationRequested();
                var (key, request) = queue[index];
                try
                {
                    run.MdbCalls++;
                    using (var perCall = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        perCall.CancelAfter(callTimeout);
                        store.Data.Ratings[key] = await mdb.GetRatingsAsync(request.Provider, request.MediaType, request.Id, perCall.Token).ConfigureAwait(false);
                    }

                    index++;
                    limitedInARow = 0;
                    failedInARow = 0;
                    await Task.Delay(Scaled(pace), ct).ConfigureAwait(false);
                }
                catch (RateLimitedException ex)
                {
                    // "Too many requests" is usually a short-term speed limit, not the end of the day's quota.
                    // Wait, check the real quota, then retry the same title at a slower pace.
                    limitedInARow++;
                    if (limitedInARow > Backoff.Length)
                    {
                        run.MdbStopped = true;
                        this.logger.Warn("{0}. Still limited after {1} waits; the rest continue next run.", ex.Message, Backoff.Length);
                        break;
                    }

                    var quota = await this.TryGetQuotaAsync(mdb, store, ct).ConfigureAwait(false);
                    if (quota != null)
                    {
                        var left = Math.Max(0, quota.Remaining - options.MdbListReserve);
                        if (left == 0)
                        {
                            run.MdbStopped = true;
                            this.logger.Info("MDBList daily quota used ({0} of {1}, {2} kept in reserve); the rest continue tomorrow.", quota.UsedToday, quota.DailyLimit, options.MdbListReserve);
                            break;
                        }

                        run.MdbBudget = Math.Min(run.MdbBudget, run.MdbCalls + left);
                    }

                    var wait = ex.RetryAfter.HasValue
                        ? TimeSpan.FromSeconds(Math.Clamp(ex.RetryAfter.Value.TotalSeconds, 5, 600))
                        : Backoff[limitedInARow - 1];
                    pace = TimeSpan.FromMilliseconds(Math.Min(pace.TotalMilliseconds * 2, MaxPace.TotalMilliseconds));
                    this.logger.Info(
                        "{0}. Daily quota is fine{1}; waiting {2:0} s, then continuing at one call every {3:0.0} s.",
                        ex.Message,
                        quota == null ? " (as far as known)" : $" ({quota.UsedToday} of {quota.DailyLimit} used)",
                        wait.TotalSeconds,
                        pace.TotalSeconds);
                    store.Save();
                    await Task.Delay(Scaled(wait), ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    run.MdbStopped = true;
                    this.logger.Error(ex.Message);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // Timeouts and network errors: skip this title (it's retried next run) and slow down a little.
                    index++;
                    failedInARow++;
                    var reason = ex is OperationCanceledException ? $"no answer within {callTimeout.TotalSeconds:0} s" : ex.Message;
                    this.logger.Warn("MDBList lookup failed for {0}: {1}", request.Name, reason);
                    if (failedInARow >= MaxFailuresInARow)
                    {
                        run.MdbStopped = true;
                        this.logger.Warn("MDBList failed {0} times in a row; skipping the rest of the ratings this run.", failedInARow);
                        break;
                    }

                    await Task.Delay(Scaled(TimeSpan.FromSeconds(5 * failedInARow)), ct).ConfigureAwait(false);
                }

                progress.Report(40.0 * index / Math.Max(1, limit));
                if (limitedInARow == 0 && (index - lastSavedIndex >= 100 || DateTime.UtcNow - lastSaved > TimeSpan.FromMinutes(1)))
                {
                    store.Save();
                    lastSavedIndex = index;
                    lastSaved = DateTime.UtcNow;
                }

                if (index > 0 && index % 1000 == 0 && index != lastReported)
                {
                    lastReported = index;
                    this.logger.Info("MDBList: {0} of {1} titles fetched so far this run", index, limit);
                }
            }

            run.MdbFetched = index;
            run.MdbSkipped = Math.Max(0, queue.Count - index);
            store.Save();
        }

        // Pacing for MDBList. Scale exists so tests can run the waits instantly.
        private static readonly TimeSpan BasePace = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan MaxPace = TimeSpan.FromSeconds(2);
        private static TimeSpan callTimeout = TimeSpan.FromSeconds(20);
        private const int MaxFailuresInARow = 10;
        private static readonly TimeSpan[] Backoff = { TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(240) };
        private static double delayScale = 1.0;

        private static TimeSpan Scaled(TimeSpan t) => TimeSpan.FromMilliseconds(t.TotalMilliseconds * delayScale);

        private async Task<MdbListQuota> TryGetQuotaAsync(MdbListClient mdb, OverlayStore store, CancellationToken ct)
        {
            try
            {
                var quota = await mdb.GetQuotaAsync(ct).ConfigureAwait(false);
                if (quota != null)
                {
                    store.Data.MdbListQuota = quota;
                }

                return quota;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                this.logger.Debug("MDBList quota check failed: {0}", ex.Message);
                return null;
            }
        }

        /// <summary>Copy cached ratings onto an item's overlay data (no network).</summary>
        private static void ApplyRatings(ItemOverlayData data, BaseItem item, string mediaType, OverlayStore store, PluginOptions options)
        {
            var (key, _, _) = RatingsKey(item, mediaType);
            if (key != null && store.Data.Ratings.TryGetValue(key, out var entry) && !entry.NotFound)
            {
                data.Imdb = entry.Imdb;
                data.RtCritic = entry.RtCritic;
                data.RtAudience = entry.RtAudience;
            }

            // IMDb's own daily file wins over MDBList's copy when it's the chosen source.
            var imdbId = item.GetProviderId(MetadataProviders.Imdb)?.Trim();
            if (options.ImdbSource == ImdbRatingSource.ImdbDataset
                && !string.IsNullOrEmpty(imdbId)
                && store.Data.ImdbRatings.TryGetValue(imdbId, out var imdb))
            {
                data.Imdb = imdb.Rating;
            }

            // Emby's community rating (TMDB's score with the default metadata providers). No API call needed.
            if (item.CommunityRating.HasValue && item.CommunityRating.Value > 0 && item.CommunityRating.Value <= 10)
            {
                data.Tmdb = Math.Round(item.CommunityRating.Value, 1);
            }
        }

        /// <summary>MDBList is only needed for Rotten Tomatoes, or for IMDb when MDBList is the chosen IMDb source.</summary>
        private static bool NeedsMdbList(PluginOptions o) =>
            o.ShowRtCritic || o.ShowRtAudience || (o.ShowImdb && o.ImdbSource == ImdbRatingSource.MdbList);

        /// <summary>Emby's own key for "these items are versions of one title"; falls back to the IMDb id.</summary>
        private static string VersionGroupKey(BaseItem movie)
        {
            var key = movie.PresentationUniqueKey;
            if (!string.IsNullOrWhiteSpace(key))
            {
                return "p:" + key;
            }

            var imdb = movie.GetProviderId(MetadataProviders.Imdb);
            return string.IsNullOrWhiteSpace(imdb) ? "i:" + movie.InternalId : "imdb:" + imdb.Trim();
        }

        /// <summary>
        /// One quality per episode: versions of the same episode (1080p and 4K files of S01E01) are merged to their
        /// best, so an extra version never counts twice.
        /// </summary>
        private static List<QualityInfo> PerEpisode(IEnumerable<BaseItem> episodes, Func<BaseItem, QualityInfo> qualityOf)
        {
            return episodes
                .GroupBy(e => e.IndexNumber.HasValue ? $"{e.ParentIndexNumber}:{e.IndexNumber}" : "id:" + e.InternalId)
                .Select(g => MediaInfoClassifier.Summarize(g.Select(qualityOf).ToList(), mostCommon: false))
                .ToList();
        }

        private async Task RefreshImdbDatasetAsync(IReadOnlyList<BaseItem> movies, IReadOnlyList<Series> series, string dataDirectory, OverlayStore store, CancellationToken ct)
        {
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in movies.Concat<BaseItem>(series))
            {
                var id = item.GetProviderId(MetadataProviders.Imdb);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    wanted.Add(id.Trim());
                }
            }

            try
            {
                var (found, downloaded) = await ImdbDataset.RefreshAsync(Path.Combine(dataDirectory, "imdb-title-ratings.tsv.gz"), wanted, store, ct).ConfigureAwait(false);
                this.logger.Info(
                    "IMDb ratings {0}: {1} of {2} library titles found",
                    downloaded ? "downloaded from IMDb" : "loaded from today's copy",
                    found,
                    wanted.Count);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                this.logger.Warn("Could not update IMDb ratings from {0}: {1}. Using the last copy.", ImdbDataset.Url, ex.Message);
            }
        }

        private BaseItem[] Query(string type, BaseItem parent = null)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { type },
                Recursive = true,
                IsVirtualItem = false,
            };

            if (parent != null)
            {
                query.Parent = parent;
            }

            return this.libraryManager.GetItemList(query);
        }

        private sealed class RunState
        {
            public bool NeedMdbList { get; set; }

            public int MdbBudget { get; set; }

            public int MdbSkipped { get; set; }

            public int MdbFetched { get; set; }

            public int MdbCalls { get; set; }

            public bool MdbStopped { get; set; }

            public int TmdbCalls { get; set; }

            public bool TmdbStopped { get; set; }
        }
    }
}
