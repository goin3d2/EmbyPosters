using System;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using PosterOverlays.Data;
using PosterOverlays.Drawing;
using PosterOverlays.Services;

namespace PosterOverlays
{
    /// <summary>
    /// Emby calls this when it serves a primary image. It never touches the original artwork: the result goes into
    /// Emby's image cache, keyed by GetConfigurationCacheKey. Everything here is a dictionary lookup; the data is
    /// gathered ahead of time by RefreshOverlayDataTask.
    /// </summary>
    public class PosterOverlayEnhancer : IImageEnhancer
    {
        // Run after other enhancers (e.g. Cover Art) so the overlay sits on top.
        public MetadataProviderPriority Priority => MetadataProviderPriority.Last;

        public bool Supports(BaseItem item, ImageType imageType)
        {
            return imageType == ImageType.Primary && this.GetSpec(item) is { IsEmpty: false };
        }

        public string GetConfigurationCacheKey(BaseItem item, ImageType imageType)
        {
            var plugin = Plugin.Instance;
            var spec = imageType == ImageType.Primary ? this.GetSpec(item) : null;
            if (plugin == null || spec == null || spec.IsEmpty)
            {
                return string.Empty;
            }

            return "po1-" + plugin.StyleHash + "-" + spec.ContentKey();
        }

        public ImageSize GetEnhancedImageSize(BaseItem item, ImageType imageType, int imageIndex, ImageSize originalImageSize)
        {
            return originalImageSize;
        }

        public EnhancedImageInfo GetEnhancedImageInfo(BaseItem item, string inputFile, ImageType imageType, int imageIndex)
        {
            return new EnhancedImageInfo { RequiresTransparency = false };
        }

        public Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, int imageIndex)
        {
            var plugin = Plugin.Instance;
            var spec = this.GetSpec(item);
            if (plugin == null || spec == null || spec.IsEmpty)
            {
                System.IO.File.Copy(inputFile, outputFile, true);
                return Task.CompletedTask;
            }

            try
            {
                OverlayRenderer.RenderFile(inputFile, outputFile, spec, plugin.Icons.Get);
            }
            catch (Exception ex)
            {
                plugin.Logger.ErrorException("Overlay failed for {0}; serving the original poster", ex, item.Name);
                System.IO.File.Copy(inputFile, outputFile, true);
            }

            return Task.CompletedTask;
        }

        private OverlaySpec GetSpec(BaseItem item)
        {
            var plugin = Plugin.Instance;
            if (plugin == null || item == null)
            {
                return null;
            }

            var options = plugin.Options;
            PosterKind kind;
            switch (item)
            {
                case Movie _ when options.EnableMovies:
                    kind = PosterKind.Movie;
                    break;
                case Series _ when options.EnableSeries:
                    kind = PosterKind.Series;
                    break;
                case Season _ when options.EnableSeasons:
                    kind = PosterKind.Season;
                    break;
                default:
                    return null;
            }

            var data = plugin.Store.GetItem(item.InternalId);
            if (data == null && kind == PosterKind.Movie)
            {
                // A movie added since the last refresh: its quality is one cheap lookup, so fill it in now.
                // Ratings arrive with the next refresh run.
                data = this.QuickMovieData(item);
            }

            return OverlaySpec.Build(data, kind, options);
        }

        private ItemOverlayData QuickMovieData(BaseItem item)
        {
            try
            {
                var quality = MediaInfoClassifier.Classify(item.GetMediaStreams(), item.Path);
                var data = new ItemOverlayData { Resolution = quality.Resolution, Hdr = quality.Hdr, Audio = quality.Audio };
                Plugin.Instance.Store.SetItem(item.InternalId, data);
                return data;
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Debug("Could not read media info for {0}: {1}", item.Name, ex.Message);
                return null;
            }
        }
    }
}
