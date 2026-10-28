using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Services;
using PosterOverlays.Data;
using PosterOverlays.Drawing;
using PosterOverlays.Icons;
using PosterOverlays.Services;
using SkiaSharp;

namespace PosterOverlays.Api
{
    [Route("/PosterOverlays/Settings", "GET", Summary = "Gets Poster Overlays settings")]
    [Authenticated(Roles = "Admin")]
    public class GetSettings : IReturnVoid
    {
    }

    [Route("/PosterOverlays/Settings", "POST", Summary = "Saves Poster Overlays settings")]
    [Authenticated(Roles = "Admin")]
    public class SaveSettings : IRequiresRequestStream, IReturnVoid
    {
        public Stream RequestStream { get; set; }
    }

    [Route("/PosterOverlays/Status", "GET", Summary = "Icon and data status for the settings page")]
    [Authenticated(Roles = "Admin")]
    public class GetStatus : IReturnVoid
    {
    }

    [Route("/PosterOverlays/PreviewItems", "GET", Summary = "Random library items with posters, for previews")]
    [Authenticated(Roles = "Admin")]
    public class GetPreviewItems : IReturnVoid
    {
    }

    [Route("/PosterOverlays/Preview", "POST", Summary = "Renders a preview poster with unsaved settings")]
    [Authenticated(Roles = "Admin")]
    public class RenderPreview : IRequiresRequestStream, IReturnVoid
    {
        public Stream RequestStream { get; set; }
    }

    public class PreviewRequest
    {
        public PluginOptions Options { get; set; }

        public PosterKind Kind { get; set; }

        /// <summary>Library item whose poster to draw on; empty for a placeholder.</summary>
        public string ItemId { get; set; }

        /// <summary>True: every badge with sample values. False: the item's real cached data.</summary>
        public bool UseSample { get; set; } = true;

        /// <summary>Banner status for sample data.</summary>
        public ShowStatus Status { get; set; } = ShowStatus.Airing;
    }

    public class PreviewItem
    {
        public string Id { get; set; }

        public string Name { get; set; }
    }

    public class PosterOverlaysApi : IService, IRequiresRequest
    {
        private const int CandidatesPerKind = 40;

        private readonly IHttpResultFactory resultFactory;
        private readonly ILibraryManager libraryManager;

        public PosterOverlaysApi(IHttpResultFactory resultFactory, ILibraryManager libraryManager)
        {
            this.resultFactory = resultFactory;
            this.libraryManager = libraryManager;
        }

        public IRequest Request { get; set; }

        private static Plugin Current => Plugin.Instance ?? throw new InvalidOperationException("Poster Overlays is not loaded");

        public object Get(GetSettings request)
        {
            return this.Json(Current.Options);
        }

        public async Task<object> Post(SaveSettings request)
        {
            var options = await ReadJsonAsync<PluginOptions>(request.RequestStream).ConfigureAwait(false)
                ?? throw new ArgumentException("No settings in the request");
            Current.SaveOptions(options);
            return this.Json(Current.Options);
        }

        public async Task<object> Get(GetStatus request)
        {
            var plugin = Current;
            var iconFiles = IconManager.SourcePaths.Keys.Count(k => plugin.Icons.Get(k) != null);
            var quota = await GetQuotaAsync(plugin).ConfigureAwait(false);
            return this.Json(new
            {
                IconsAvailable = iconFiles,
                IconsExpected = IconManager.SourcePaths.Count,
                IconsFolder = plugin.Icons.Folder,
                ItemsCached = plugin.Store.Data.Items.Count,
                RatingsCached = plugin.Store.Data.Ratings.Count(r => !r.Value.NotFound),
                ImdbRatingsCached = plugin.Store.Data.ImdbRatings.Count,
                ImdbDatasetFetchedUtc = plugin.Store.Data.ImdbDatasetFetchedUtc,
                NeedsMdbList = plugin.Options.ShowRtCritic || plugin.Options.ShowRtAudience
                    || (plugin.Options.ShowImdb && plugin.Options.ImdbSource == ImdbRatingSource.MdbList),
                TmdbConfigured = !string.IsNullOrWhiteSpace(plugin.Options.TmdbApiKey),
                MdbListConfigured = !string.IsNullOrWhiteSpace(plugin.Options.MdbListApiKey),
                MdbListDailyLimit = quota?.DailyLimit,
                MdbListUsedToday = quota?.UsedToday,
                MdbListReserve = plugin.Options.MdbListReserve,
                Version = plugin.Version?.ToString(),
            });
        }

        private static MdbListQuota cachedQuota;
        private static string cachedQuotaKey;

        /// <summary>Live MDBList quota for the status line, checked at most once a minute.</summary>
        private static async Task<MdbListQuota> GetQuotaAsync(Plugin plugin)
        {
            var key = plugin.Options.MdbListApiKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var cached = cachedQuota;
            if (cached != null && cachedQuotaKey == key && DateTime.UtcNow - cached.CheckedUtc < TimeSpan.FromMinutes(1))
            {
                return cached;
            }

            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
                var quota = await new MdbListClient(key).GetQuotaAsync(cts.Token).ConfigureAwait(false);
                if (quota != null)
                {
                    cachedQuota = quota;
                    cachedQuotaKey = key;
                    plugin.Store.Data.MdbListQuota = quota;
                    return quota;
                }
            }
            catch (Exception ex)
            {
                plugin.Logger.Debug("MDBList quota check failed: {0}", ex.Message);
            }

            return plugin.Store.Data.MdbListQuota;
        }

        public object Get(GetPreviewItems request)
        {
            return this.Json(new
            {
                Movie = this.PickItems("Movie"),
                Series = this.PickItems("Series"),
                Season = this.PickItems("Season"),
            });
        }

        public async Task<object> Post(RenderPreview request)
        {
            var plugin = Current;
            var preview = await ReadJsonAsync<PreviewRequest>(request.RequestStream).ConfigureAwait(false) ?? new PreviewRequest();
            var options = (preview.Options ?? plugin.Options).Normalize();

            // Previews ignore the per-type switches so turning one off still shows what the others look like.
            options.EnableMovies = options.EnableSeries = options.EnableSeasons = true;

            var item = this.FindItem(preview.ItemId);
            ItemOverlayData data;
            if (preview.UseSample || item == null)
            {
                data = PreviewRenderer.SampleData(preview.Status);
            }
            else
            {
                data = plugin.Store.GetItem(item.InternalId);
                if (data == null && item is Movie)
                {
                    var q = MediaInfoClassifier.Classify(item.GetMediaStreams(), item.Path);
                    data = new ItemOverlayData { Resolution = q.Resolution, Hdr = q.Hdr, Audio = q.Audio };
                }
            }

            SKBitmap poster = null;
            try
            {
                var path = PosterPath(item);
                if (path != null)
                {
                    poster = SKBitmap.Decode(path);
                }

                var png = PreviewRenderer.RenderPng(poster, preview.Kind, options, data, plugin.Icons.Get);
                return this.resultFactory.GetResult(this.Request, new ReadOnlyMemory<byte>(png), "image/png", new Dictionary<string, string> { ["Cache-Control"] = "no-store" });
            }
            finally
            {
                poster?.Dispose();
            }
        }

        private List<PreviewItem> PickItems(string type)
        {
            var ids = this.libraryManager.GetInternalItemIds(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { type },
                Recursive = true,
                IsVirtualItem = false,
            });

            var random = new Random();
            var picked = new List<PreviewItem>();
            foreach (var id in ids.OrderBy(_ => random.Next()).Take(CandidatesPerKind * 3))
            {
                var item = this.libraryManager.GetItemById(id);
                if (PosterPath(item) == null)
                {
                    continue;
                }

                var name = item is Season season ? $"{season.SeriesName} – {item.Name}" : item.Name;
                picked.Add(new PreviewItem { Id = id.ToString(System.Globalization.CultureInfo.InvariantCulture), Name = name });
                if (picked.Count >= CandidatesPerKind)
                {
                    break;
                }
            }

            return picked;
        }

        private BaseItem FindItem(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            if (long.TryParse(id, out var internalId))
            {
                return this.libraryManager.GetItemById(internalId);
            }

            return Guid.TryParse(id, out var guid) ? this.libraryManager.GetItemById(guid) : null;
        }

        /// <summary>Local primary image file, or null (remote or missing images are skipped).</summary>
        private static string PosterPath(BaseItem item)
        {
            if (item == null || !(item is Movie || item is Series || item is Season))
            {
                return null;
            }

            var image = item.GetImageInfo(ImageType.Primary, 0);
            return image != null && image.IsLocalFile && File.Exists(image.Path) ? image.Path : null;
        }

        private static async Task<T> ReadJsonAsync<T>(Stream stream)
            where T : class
        {
            if (stream == null)
            {
                return null;
            }

            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync().ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<T>(text, Plugin.SettingsJson);
        }

        private object Json(object value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Plugin.SettingsJson);
            return this.resultFactory.GetResult(this.Request, new ReadOnlyMemory<byte>(bytes), "application/json", new Dictionary<string, string> { ["Cache-Control"] = "no-store" });
        }
    }
}
