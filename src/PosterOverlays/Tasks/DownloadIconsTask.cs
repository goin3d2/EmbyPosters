using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace PosterOverlays.Tasks
{
    /// <summary>Downloads the icon pack into the plugin's icons folder. Runs once at startup if no icons exist yet.</summary>
    public class DownloadIconsTask : IScheduledTask
    {
        private static int hasRunSinceStartup;

        private readonly ILogger logger;

        public DownloadIconsTask(ILogManager logManager)
        {
            this.logger = logManager.GetLogger(Plugin.PluginName);
        }

        public string Name => "Download poster overlay icons";

        public string Key => "PosterOverlaysDownloadIcons";

        public string Description => "Downloads badge icons (resolution, HDR, audio, IMDb, Rotten Tomatoes) from the icon source URL in the plugin settings, replacing existing downloads. Files in icons/custom are never touched.";

        public string Category => Plugin.PluginName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[] { new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerStartup } };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("Poster Overlays plugin is not loaded");

            // The first run after the server starts (the startup trigger) only fills in missing files.
            // Running the task again by hand re-downloads everything.
            var force = Interlocked.Exchange(ref hasRunSinceStartup, 1) == 1;
            var (downloaded, failed) = await plugin.Icons
                .DownloadAsync(plugin.Options.IconSourceBaseUrl, force, progress, cancellationToken)
                .ConfigureAwait(false);

            plugin.InvalidateStyle();
            this.logger.Info("Poster overlay icons: {0} downloaded, {1} failed, stored in {2}", downloaded, failed, plugin.Icons.Folder);
        }
    }
}
