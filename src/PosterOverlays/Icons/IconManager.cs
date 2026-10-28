using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using SkiaSharp;

namespace PosterOverlays.Icons
{
    /// <summary>
    /// Icons live in [plugin data]/icons/{key}.png (downloaded) and [plugin data]/icons/custom/{key}.png (yours, always wins).
    /// Nothing is downloaded at render time.
    /// </summary>
    public sealed class IconManager
    {
        /// <summary>Icon key to path under the icon source base URL (Kometa's defaults/overlays/images layout).</summary>
        public static readonly IReadOnlyDictionary<string, string> SourcePaths = new Dictionary<string, string>
        {
            ["res_4k"] = "resolution/4k.png",
            ["res_1080p"] = "resolution/1080p.png",
            ["res_720p"] = "resolution/720p.png",
            ["res_576p"] = "resolution/576p.png",
            ["res_480p"] = "resolution/480p.png",
            ["hdr_dv"] = "resolution/dv.png",
            ["hdr_hdr10plus"] = "resolution/plus.png",
            ["hdr_hdr10"] = "resolution/hdr.png",
            ["hdr_hlg"] = "resolution/hlg.png",
            ["audio_truehd_atmos"] = "audio_codec/compact/truehd_atmos.png",
            ["audio_plus_atmos"] = "audio_codec/compact/plus_atmos.png",
            ["audio_atmos"] = "audio_codec/compact/atmos.png",
            ["audio_dtsx"] = "audio_codec/compact/dtsx.png",
            ["audio_dtshdma"] = "audio_codec/compact/ma.png",
            ["audio_truehd"] = "audio_codec/compact/truehd.png",
            ["audio_ddplus"] = "audio_codec/compact/plus.png",
            ["audio_dts"] = "audio_codec/compact/dts.png",
            ["audio_dd"] = "audio_codec/compact/digital.png",
            ["imdb"] = "rating/IMDb.png",
            ["tmdb"] = "rating/TMDb.png",
            ["rt_critic_fresh"] = "rating/RT-Crit-Fresh.png",
            ["rt_critic_rotten"] = "rating/RT-Crit-Rotten.png",
            ["rt_audience_fresh"] = "rating/RT-Aud-Fresh.png",
            ["rt_audience_rotten"] = "rating/RT-Aud-Rotten.png",
        };

        private readonly string folder;
        private readonly string customFolder;
        private readonly ILogger logger;
        private readonly ConcurrentDictionary<string, SKImage> cache = new ConcurrentDictionary<string, SKImage>();
        private readonly ConcurrentDictionary<string, bool> missing = new ConcurrentDictionary<string, bool>();
        private int generation;

        public IconManager(string folder, ILogger logger)
        {
            this.folder = folder;
            this.customFolder = Path.Combine(folder, "custom");
            this.logger = logger;
            Directory.CreateDirectory(this.customFolder);
            this.generation = this.ComputeGeneration();
        }

        public string Folder => this.folder;

        /// <summary>Changes whenever icon files change, so it can go into the image cache key.</summary>
        public int Generation => this.generation;

        public bool HasAnyIcons => SourcePaths.Keys.Any(k => File.Exists(this.PathFor(k)));

        /// <summary>Returns the icon, or null if neither a custom nor a downloaded file exists.</summary>
        public SKImage Get(string key)
        {
            if (key == null || this.missing.ContainsKey(key))
            {
                return null;
            }

            return this.cache.GetOrAdd(key, k =>
            {
                var path = this.PathFor(k);
                if (!File.Exists(path))
                {
                    this.missing[k] = true;
                    return null;
                }

                try
                {
                    using var data = SKData.Create(path);
                    var image = SKImage.FromEncodedData(data);
                    if (image == null)
                    {
                        this.logger.Warn("Icon {0} could not be decoded", path);
                        this.missing[k] = true;
                    }

                    return image;
                }
                catch (Exception ex)
                {
                    this.logger.ErrorException("Icon {0} could not be loaded", ex, path);
                    this.missing[k] = true;
                    return null;
                }
            });
        }

        public void ReloadAll()
        {
            this.cache.Clear();
            this.missing.Clear();
            this.generation = this.ComputeGeneration();
        }

        /// <summary>Download every mapped icon from baseUrl. Existing files are replaced only when force is set.</summary>
        public async Task<(int Downloaded, int Failed)> DownloadAsync(string baseUrl, bool force, IProgress<double> progress, CancellationToken ct)
        {
            baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (baseUrl.Length == 0)
            {
                throw new InvalidOperationException("No icon source URL is set in the plugin settings.");
            }

            Directory.CreateDirectory(this.folder);
            int done = 0, downloaded = 0, failed = 0;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            foreach (var pair in SourcePaths)
            {
                ct.ThrowIfCancellationRequested();
                var target = Path.Combine(this.folder, pair.Key + ".png");
                if (force || !File.Exists(target))
                {
                    var url = baseUrl + "/" + string.Join("/", pair.Value.Split('/').Select(Uri.EscapeDataString));
                    try
                    {
                        var bytes = await client.GetByteArrayAsync(url, ct).ConfigureAwait(false);
                        using (var probe = SKImage.FromEncodedData(bytes))
                        {
                            if (probe == null)
                            {
                                throw new InvalidDataException("not an image");
                            }
                        }

                        var tmp = target + ".tmp";
                        await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
                        File.Move(tmp, target, true);
                        downloaded++;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        failed++;
                        this.logger.Warn("Icon {0} could not be downloaded from {1}: {2}", pair.Key, url, ex.Message);
                    }
                }

                progress?.Report(100.0 * ++done / SourcePaths.Count);
            }

            this.ReloadAll();
            return (downloaded, failed);
        }

        private string PathFor(string key)
        {
            var custom = Path.Combine(this.customFolder, key + ".png");
            return File.Exists(custom) ? custom : Path.Combine(this.folder, key + ".png");
        }

        private int ComputeGeneration()
        {
            unchecked
            {
                var hash = 17;
                foreach (var dir in new[] { this.folder, this.customFolder })
                {
                    if (!Directory.Exists(dir))
                    {
                        continue;
                    }

                    foreach (var file in Directory.EnumerateFiles(dir, "*.png").OrderBy(f => f, StringComparer.Ordinal))
                    {
                        // Stable across restarts (string.GetHashCode is randomized per process in .NET).
                        var info = new FileInfo(file);
                        foreach (var ch in dir.Length + info.Name)
                        {
                            hash = (hash * 31) + ch;
                        }

                        hash = (hash * 31) + (int)info.Length;
                        hash = (hash * 31) + (int)(info.LastWriteTimeUtc.Ticks / TimeSpan.TicksPerSecond);
                    }
                }

                return hash;
            }
        }
    }
}
