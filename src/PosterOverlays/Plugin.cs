using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using PosterOverlays.Data;
using PosterOverlays.Icons;

namespace PosterOverlays
{
    public class Plugin : BasePlugin, IHasWebPages, IHasThumbImage
    {
        public const string PluginName = "Poster Overlays";

        public static readonly JsonSerializerOptions SettingsJson = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private static readonly Guid PluginId = new Guid("5cf9f1d3-5cc9-4353-b063-0f11fd632793");

        private readonly ILogger logger;
        private readonly object sync = new object();
        private OverlayStore store;
        private IconManager icons;
        private PluginOptions options;
        private string styleHash;

        public Plugin(ILogManager logManager)
        {
            Instance = this;
            this.logger = logManager.GetLogger(PluginName);
            this.logger.Info("{0} loaded", PluginName);
        }

        public static Plugin Instance { get; private set; }

        public override string Description =>
            "Full-width series status banners (Airing, Returning, Ended, Canceled) and a stack of quality and rating badges on posters.";

        public override Guid Id => PluginId;

        public override string Name => PluginName;

        public ILogger Logger => this.logger;

        /// <summary>The plugin's own data folder (settings, data cache and icons).</summary>
        public string DataDirectory
        {
            get
            {
                var dir = this.DataFolderPath;
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public PluginOptions Options
        {
            get
            {
                var current = this.options;
                if (current != null)
                {
                    return current;
                }

                lock (this.sync)
                {
                    return this.options ??= this.LoadOptions();
                }
            }
        }

        public OverlayStore Store
        {
            get
            {
                lock (this.sync)
                {
                    return this.store ??= OverlayStore.Load(Path.Combine(this.DataDirectory, "overlay-data.json"), this.logger);
                }
            }
        }

        public IconManager Icons
        {
            get
            {
                lock (this.sync)
                {
                    return this.icons ??= new IconManager(Path.Combine(this.DataDirectory, "icons"), this.logger);
                }
            }
        }

        /// <summary>
        /// Short hash of every option that changes how a poster looks, plus the icon files. Part of the image cache
        /// key, so saving new styling makes Emby re-render posters.
        /// </summary>
        public string StyleHash
        {
            get
            {
                var hash = this.styleHash;
                if (hash == null)
                {
                    var json = JsonSerializer.Serialize(this.Options.GetStyleSnapshot());
                    var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(json + "|" + this.Icons.Generation));
                    hash = this.styleHash = Convert.ToHexString(bytes, 0, 5);
                }

                return hash;
            }
        }

        private string SettingsPath => Path.Combine(this.DataDirectory, "settings.json");

        public void InvalidateStyle()
        {
            this.styleHash = null;
        }

        public void SaveOptions(PluginOptions newOptions)
        {
            newOptions.Normalize();
            lock (this.sync)
            {
                var tmp = this.SettingsPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(newOptions, SettingsJson));
                File.Move(tmp, this.SettingsPath, true);
                this.options = newOptions;
            }

            this.InvalidateStyle();
            this.Icons.ReloadAll();
            this.logger.Info("{0} settings saved; posters will re-render on next request", PluginName);
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "posteroverlays",
                    DisplayName = PluginName,
                    EmbeddedResourcePath = this.GetType().Namespace + ".Configuration.configPage.html",
                    IsMainConfigPage = true,
                },
                new PluginPageInfo
                {
                    Name = "posteroverlaysjs",
                    EmbeddedResourcePath = this.GetType().Namespace + ".Configuration.configPage.js",
                },
            };
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public Stream GetThumbImage()
        {
            return this.GetType().Assembly.GetManifestResourceStream("PosterOverlays.ThumbImage.png");
        }

        private PluginOptions LoadOptions()
        {
            try
            {
                if (File.Exists(this.SettingsPath))
                {
                    var text = File.ReadAllText(this.SettingsPath);
                    var loaded = JsonSerializer.Deserialize<PluginOptions>(text, SettingsJson);
                    if (loaded != null)
                    {
                        // 0.2.0 saved its default per-run cap of 900; the quota check now does that job, so drop it.
                        if (!text.Contains("\"MdbListReserve\"") && loaded.MaxMdbListCallsPerRun == 900)
                        {
                            loaded.MaxMdbListCallsPerRun = 0;
                        }

                        return loaded.Normalize();
                    }
                }
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("Could not read {0}; using defaults", ex, this.SettingsPath);
            }

            return new PluginOptions();
        }
    }
}
