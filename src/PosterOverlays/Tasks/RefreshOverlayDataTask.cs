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
                new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerInterval, IntervalTicks = TimeSpan.FromHours(24).Ticks },
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("Poster Overlays plugin is not loaded");
            var options = plugin.Options;
            var store = plugin.Store;
            var tmdb = new TmdbClient(options.TmdbApiKey);
            var mdb = new MdbListClient(options.MdbListApiKey);
            var run = new RunState { Store = store };
            var mostCommon = options.SeriesQualityRule == SeriesQualityRule.MostEpisodes;
            var started = DateTime.UtcNow;

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

            // Only small records are kept for the whole run; Emby's full items are loaded a page at a time.
            var movieIds = options.EnableMovies ? this.Ids("Movie") : Array.Empty<long>();
            var seriesIds = options.EnableSeries || options.EnableSeasons ? this.Ids("Series") : Array.Empty<long>();
            var movies = this.LoadTitles(movieIds, cancellationToken);
            var series = this.LoadTitles(seriesIds, cancellationToken);
            var total = Math.Max(1, movies.Count + series.Count);
            var done = 0;

            // Quality only needs reading again for items Emby has saved since the last complete run (new files,
            // new media info). A different quality rule or plugin version reads everything again.
            var rulesKey = Plugin.Instance.Version + "/" + options.SeriesQualityRule;
            var since = store.Data.QualityRulesKey == rulesKey ? store.Data.QualityCheckedUtc : null;
            var changed = since.HasValue ? this.ChangedSince(since.Value.AddMinutes(-10)) : null;

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
            var toRead = new List<List<Title>>();
            var pending = 0;
            foreach (var versions in movies.GroupBy(m => m.VersionKey).Select(g => g.ToList()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cached = versions.Select(v => store.GetItem(v.InternalId)).ToList();
                var fresh = changed != null
                    && versions.All(v => !changed.Contains(v.InternalId))
                    && cached.All(c => c != null && c.Resolution != null);
                if (fresh)
                {
                    for (var i = 0; i < versions.Count; i++)
                    {
                        this.SetMovie(versions[i], cached[i], store, options);
                    }

                    run.Reused += versions.Count;
                    done += versions.Count;
                    progress.Report(40 + (60.0 * done / total));
                    continue;
                }

                toRead.Add(versions);
                pending += versions.Count;
                if (pending >= PageSize)
                {
                    done += this.ReadMovies(toRead, store, options, run, cancellationToken);
                    toRead.Clear();
                    pending = 0;
                    progress.Report(40 + (60.0 * done / total));
                }

                run.SaveIfDue();
            }

            done += this.ReadMovies(toRead, store, options, run, cancellationToken);

            var seasons = options.EnableSeasons ? this.LoadSeasons(cancellationToken) : new Dictionary<long, List<SeasonRef>>();
            var changedShows = changed == null ? null : this.ShowsWithChanges(changed, cancellationToken);
            foreach (var show in series)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    seasons.TryGetValue(show.InternalId, out var showSeasons);
                    var reread = changedShows == null
                        || changedShows.Contains(show.InternalId)
                        || (options.EnableSeries && store.GetItem(show.InternalId) == null)
                        || (showSeasons ?? new List<SeasonRef>()).Any(x => store.GetItem(x.InternalId) == null);
                    await this.RefreshSeriesAsync(show, showSeasons ?? new List<SeasonRef>(), reread, tmdb, store, run, options, mostCommon, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    this.logger.ErrorException("Poster overlay data failed for series {0}", ex, show.Name);
                }

                progress.Report(40 + (60.0 * ++done / total));
                run.SaveIfDue();
            }

            // Forget items that no longer exist (or whose type was switched off).
            var keep = new HashSet<long>(movieIds);
            if (options.EnableSeries)
            {
                keep.UnionWith(seriesIds);
            }

            foreach (var list in seasons.Values)
            {
                keep.UnionWith(list.Select(x => x.InternalId));
            }

            foreach (var stale in store.Data.Items.Keys.Where(k => !keep.Contains(k)).ToList())
            {
                store.Data.Items.TryRemove(stale, out _);
            }

            if (store.Data.MdbListQuota != null && run.MdbCalls > 0)
            {
                // Keep the shown usage roughly current until the next check.
                store.Data.MdbListQuota.UsedToday += run.MdbCalls;
            }

            store.Data.QualityCheckedUtc = started;
            store.Data.QualityRulesKey = rulesKey;
            store.Save();
            this.logger.Info(
                "Poster overlay data refreshed in {7:0} min: {0} movies, {1} series ({8} items re-read, {9} unchanged); {2} TMDB calls; MDBList: {3} titles updated in {4} calls, {5} still waiting{6}",
                movies.Count,
                series.Count,
                run.TmdbCalls,
                run.MdbFetched,
                run.MdbCalls,
                run.MdbSkipped,
                run.MdbStopped ? " (stopped early; the rest continue next run)" : string.Empty,
                (DateTime.UtcNow - started).TotalMinutes,
                run.Reread,
                run.Reused);
        }

        private const int PageSize = 500;

        /// <summary>Reads media info for the movie version sets waiting, a page of items at a time. Returns how many items.</summary>
        private int ReadMovies(List<List<Title>> groups, OverlayStore store, PluginOptions options, RunState run, CancellationToken ct)
        {
            var count = 0;
            if (groups.Count == 0)
            {
                return 0;
            }

            var items = this.LoadItems(groups.SelectMany(g => g).Select(t => t.InternalId).ToArray()).ToDictionary(i => i.InternalId);
            foreach (var versions in groups)
            {
                ct.ThrowIfCancellationRequested();
                var perVersion = new List<QualityInfo>();
                foreach (var v in versions)
                {
                    if (!items.TryGetValue(v.InternalId, out var movie))
                    {
                        continue;
                    }

                    try
                    {
                        perVersion.Add(MediaInfoClassifier.Classify(movie.GetMediaStreams(ct), movie.Path));
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        this.logger.ErrorException("Could not read media info for movie {0}", ex, movie.Name);
                    }
                }

                var quality = MediaInfoClassifier.Summarize(perVersion, mostCommon: false);
                var data = new ItemOverlayData { Resolution = quality.Resolution, Hdr = quality.Hdr, Audio = quality.Audio };
                foreach (var v in versions)
                {
                    this.SetMovie(v, data, store, options);
                }

                count += versions.Count;
                run.Reread += versions.Count;
            }

            return count;
        }

        private void SetMovie(Title movie, ItemOverlayData quality, OverlayStore store, PluginOptions options)
        {
            var data = new ItemOverlayData { Resolution = quality?.Resolution, Hdr = quality?.Hdr, Audio = quality?.Audio };
            ApplyRatings(data, movie, "movie", store, options);
            store.SetItem(movie.InternalId, data);
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
            Title show,
            List<SeasonRef> seasons,
            bool reread,
            TmdbClient tmdb,
            OverlayStore store,
            RunState run,
            PluginOptions options,
            bool mostCommon,
            CancellationToken ct)
        {
            var status = await this.GetStatusAsync(show, tmdb, store, run, options, ct).ConfigureAwait(false);
            var ratings = new ItemOverlayData();
            ApplyRatings(ratings, show, "show", store, options);
            ItemOverlayData With(QualityInfo q) => new ItemOverlayData
            {
                Status = status,
                Resolution = q?.Resolution,
                Hdr = q?.Hdr,
                Audio = q?.Audio,
                Imdb = ratings.Imdb,
                RtCritic = ratings.RtCritic,
                RtAudience = ratings.RtAudience,
                Tmdb = ratings.Tmdb,
            };
            QualityInfo Cached(long id)
            {
                var c = store.GetItem(id);
                return c == null ? null : new QualityInfo { Resolution = c.Resolution, Hdr = c.Hdr, Audio = c.Audio };
            }

            if (!reread)
            {
                // Nothing in this show changed since the last run: keep its quality, update status and ratings.
                if (options.EnableSeries)
                {
                    store.SetItem(show.InternalId, With(Cached(show.InternalId)));
                }

                foreach (var season in seasons)
                {
                    store.SetItem(season.InternalId, With(Cached(season.InternalId)));
                }

                run.Reused++;
                return;
            }

            run.Reread++;
            var parent = this.libraryManager.GetItemById(show.InternalId);
            if (parent == null)
            {
                return;
            }

            var episodes = this.Query("Episode", parent);
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

                store.SetItem(show.InternalId, With(MediaInfoClassifier.Summarize(PerEpisode(sample, QualityOf), mostCommon)));
            }

            foreach (var season in seasons)
            {
                var eps = season.IndexNumber.HasValue && perSeason.TryGetValue(season.IndexNumber.Value, out var list) ? list : new List<BaseItem>();
                store.SetItem(season.InternalId, With(MediaInfoClassifier.Summarize(PerEpisode(eps, QualityOf), mostCommon)));
            }
        }

        private async Task<ShowStatus> GetStatusAsync(Title show, TmdbClient tmdb, OverlayStore store, RunState run, PluginOptions options, CancellationToken ct)
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

        private async Task<string> ResolveTmdbIdAsync(Title show, TmdbClient tmdb, OverlayStore store, RunState run, CancellationToken ct)
        {
            var direct = show.Tmdb;
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct.Trim();
            }

            foreach (var (external, source) in new[] { (show.Tvdb, "tvdb_id"), (show.Imdb, "imdb_id") })
            {
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
        private static (string Key, string Provider, string Id) RatingsKey(Title item, string mediaType)
        {
            string provider = null, id = item.Imdb;
            if (!string.IsNullOrWhiteSpace(id))
            {
                provider = "imdb";
            }
            else
            {
                id = item.Tmdb;
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
            IReadOnlyList<Title> movies,
            IReadOnlyList<Title> series,
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
            void Consider(Title item, string mediaType)
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
                if (limitedInARow == 0 && DateTime.UtcNow - lastSaved > TimeSpan.FromMinutes(2))
                {
                    store.Save();
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
        private static void ApplyRatings(ItemOverlayData data, Title item, string mediaType, OverlayStore store, PluginOptions options)
        {
            var (key, _, _) = RatingsKey(item, mediaType);
            if (key != null && store.Data.Ratings.TryGetValue(key, out var entry) && !entry.NotFound)
            {
                data.Imdb = entry.Imdb;
                data.RtCritic = entry.RtCritic;
                data.RtAudience = entry.RtAudience;
            }

            // IMDb's own daily file wins over MDBList's copy when it's the chosen source.
            var imdbId = item.Imdb?.Trim();
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

        private async Task RefreshImdbDatasetAsync(IReadOnlyList<Title> movies, IReadOnlyList<Title> series, string dataDirectory, OverlayStore store, CancellationToken ct)
        {
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in movies.Concat(series))
            {
                var id = item.Imdb;
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

        private long[] Ids(string type, DateTimeOffset? savedSince = null)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { type },
                Recursive = true,
                IsVirtualItem = false,
            };

            if (savedSince.HasValue)
            {
                query.MinDateLastSaved = savedSince.Value;
            }

            return this.libraryManager.GetInternalItemIds(query);
        }

        private BaseItem[] LoadItems(long[] ids)
        {
            return ids.Length == 0 ? Array.Empty<BaseItem>() : this.libraryManager.GetItemList(new InternalItemsQuery { ItemIds = ids });
        }

        /// <summary>Small records for every title, read a page of items at a time.</summary>
        private List<Title> LoadTitles(long[] ids, CancellationToken ct)
        {
            var list = new List<Title>(ids.Length);
            for (var i = 0; i < ids.Length; i += PageSize)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var item in this.LoadItems(ids.Skip(i).Take(PageSize).ToArray()))
                {
                    list.Add(new Title
                    {
                        InternalId = item.InternalId,
                        Name = item.Name,
                        Imdb = Clean(item.GetProviderId(MetadataProviders.Imdb)),
                        Tmdb = Clean(item.GetProviderId(MetadataProviders.Tmdb)),
                        Tvdb = Clean(item.GetProviderId(MetadataProviders.Tvdb)),
                        PremiereDate = item.PremiereDate,
                        CommunityRating = item.CommunityRating,
                        VersionKey = VersionGroupKey(item),
                        Status = (item as Series)?.Status,
                    });
                }
            }

            return list;
        }

        private static string Clean(string id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim();

        /// <summary>Every season, by the id of its show.</summary>
        private Dictionary<long, List<SeasonRef>> LoadSeasons(CancellationToken ct)
        {
            var ids = this.Ids("Season");
            var map = new Dictionary<long, List<SeasonRef>>();
            for (var i = 0; i < ids.Length; i += PageSize)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var season in this.LoadItems(ids.Skip(i).Take(PageSize).ToArray()).OfType<Season>())
                {
                    var show = season.SeriesId;
                    if (!map.TryGetValue(show, out var list))
                    {
                        map[show] = list = new List<SeasonRef>();
                    }

                    list.Add(new SeasonRef { InternalId = season.InternalId, IndexNumber = season.IndexNumber });
                }
            }

            return map;
        }

        /// <summary>Movies and episodes Emby saved since the given time (new files, new media info).</summary>
        private HashSet<long> ChangedSince(DateTime sinceUtc)
        {
            var since = new DateTimeOffset(DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc));
            var set = new HashSet<long>(this.Ids("Movie", since));
            set.UnionWith(this.Ids("Episode", since).Select(id => -id));
            return set;
        }

        /// <summary>Shows that have an episode among the changed items (stored as negative ids).</summary>
        private HashSet<long> ShowsWithChanges(HashSet<long> changed, CancellationToken ct)
        {
            var episodeIds = changed.Where(id => id < 0).Select(id => -id).ToArray();
            var shows = new HashSet<long>();
            for (var i = 0; i < episodeIds.Length; i += PageSize)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var e in this.LoadItems(episodeIds.Skip(i).Take(PageSize).ToArray()).OfType<Episode>())
                {
                    shows.Add(e.SeriesId);
                }
            }

            return shows;
        }

        /// <summary>What the run needs to know about a movie or show, without keeping Emby's item in memory.</summary>
        private sealed class Title
        {
            public long InternalId { get; set; }

            public string Name { get; set; }

            public string Imdb { get; set; }

            public string Tmdb { get; set; }

            public string Tvdb { get; set; }

            public DateTimeOffset? PremiereDate { get; set; }

            public float? CommunityRating { get; set; }

            public string VersionKey { get; set; }

            public SeriesStatus? Status { get; set; }
        }

        private sealed class SeasonRef
        {
            public long InternalId { get; set; }

            public int? IndexNumber { get; set; }
        }

        private sealed class RunState
        {
            private DateTime lastSave = DateTime.UtcNow;

            public OverlayStore Store { get; set; }

            public int Reread { get; set; }

            public int Reused { get; set; }

            /// <summary>Saves at most every two minutes, so a long run doesn't keep rewriting the whole file.</summary>
            public void SaveIfDue()
            {
                if (DateTime.UtcNow - this.lastSave > TimeSpan.FromMinutes(2))
                {
                    this.Store.Save();
                    this.lastSave = DateTime.UtcNow;
                }
            }

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
