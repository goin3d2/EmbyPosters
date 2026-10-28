using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MediaBrowser.Model.Entities;

namespace PosterOverlays.Services
{
    /// <summary>Quality of one file: resolution, HDR and audio keys (icon keys).</summary>
    public sealed class QualityInfo
    {
        public string Resolution { get; set; }

        public string Hdr { get; set; }

        public string Audio { get; set; }

        public bool IsEmpty => this.Resolution == null && this.Hdr == null && this.Audio == null;
    }

    /// <summary>Turns Emby media streams into resolution / HDR / audio keys, and summarizes many files into one.</summary>
    public static class MediaInfoClassifier
    {
        // Higher index = better.
        public static readonly string[] ResolutionRank = { "res_480p", "res_576p", "res_720p", "res_1080p", "res_4k" };
        public static readonly string[] HdrRank = { "hdr_hlg", "hdr_hdr10", "hdr_hdr10plus", "hdr_dv" };
        public static readonly string[] AudioRank =
        {
            "audio_dd", "audio_dts", "audio_ddplus", "audio_truehd", "audio_dtshdma", "audio_dtsx", "audio_atmos", "audio_plus_atmos", "audio_truehd_atmos",
        };

        private static readonly HashSet<string> BasicAudio = new HashSet<string> { "audio_dd", "audio_ddplus", "audio_dts" };

        public static bool IsBasicAudio(string key) => key != null && BasicAudio.Contains(key);

        public static QualityInfo Classify(IReadOnlyCollection<MediaStream> streams, string path)
        {
            var info = Classify(streams);
            info.Audio = UpgradeFromPath(info.Audio, path);
            return info;
        }

        public static QualityInfo Classify(IReadOnlyCollection<MediaStream> streams)
        {
            var info = new QualityInfo();
            if (streams == null || streams.Count == 0)
            {
                return info;
            }

            var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video && !IsCoverArt(s));
            if (video != null)
            {
                info.Resolution = ClassifyResolution(video.Width, video.Height);
                info.Hdr = ClassifyHdr(video);
            }

            info.Audio = streams
                .Where(s => s.Type == MediaStreamType.Audio)
                .Select(ClassifyAudio)
                .Where(k => k != null)
                .OrderByDescending(k => Array.IndexOf(AudioRank, k))
                .FirstOrDefault();

            return info;
        }

        public static string ClassifyResolution(int? width, int? height)
        {
            if (width == null && height == null)
            {
                return null;
            }

            // Width matters: 3840x1600 scope films are 4K, 1920x800 is 1080p.
            var w = width ?? 0;
            var h = height ?? 0;
            if (w >= 3200 || h >= 2000) return "res_4k";
            if (w >= 1600 || h >= 1000) return "res_1080p";
            if (w >= 1100 || h >= 700) return "res_720p";
            if (h >= 560) return "res_576p";
            if (w > 0 || h > 0) return "res_480p";
            return null;
        }

        public static string ClassifyHdr(MediaStream video)
        {
            switch (video.ExtendedVideoType)
            {
                case ExtendedVideoTypes.DolbyVision:
                    return "hdr_dv";
                case ExtendedVideoTypes.Hdr10Plus:
                    return "hdr_hdr10plus";
                case ExtendedVideoTypes.Hdr10:
                    return "hdr_hdr10";
                case ExtendedVideoTypes.HyperLogGamma:
                    return "hdr_hlg";
            }

            // Fallbacks for items probed before Emby filled ExtendedVideoType.
            var range = (video.VideoRange ?? string.Empty).ToLowerInvariant();
            var transfer = (video.ColorTransfer ?? string.Empty).ToLowerInvariant();
            if (range.Contains("dolby") || range.Contains("dovi")) return "hdr_dv";
            if (transfer == "arib-std-b67") return "hdr_hlg";
            if (transfer == "smpte2084" || range == "hdr") return "hdr_hdr10";
            return null;
        }

        // "DTS-X", "DTS:X", "DTS.X", "DTSX" but not "DTS.x264".
        private static readonly Regex DtsXPattern = new Regex(@"dts[\.\-: _]?x(?![0-9a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AtmosPattern = new Regex(@"(?<![a-z])atmos(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Emby stores no Atmos/DTS:X flag (the audio Profile is empty for TrueHD and E-AC3), so those come from the
        /// track title. Only TrueHD and E-AC3 can carry Atmos: an AAC track titled "(from E-AC3 5.1 Atmos)" is a
        /// transcode and doesn't count.
        /// </summary>
        public static string ClassifyAudio(MediaStream a)
        {
            var codec = (a.Codec ?? string.Empty).ToLowerInvariant();
            var profile = (a.Profile ?? string.Empty).ToLowerInvariant();
            var title = string.Join(" ", a.Profile, a.Title);
            var atmos = AtmosPattern.IsMatch(title);

            switch (codec)
            {
                case "truehd":
                case "mlp":
                    return atmos ? "audio_truehd_atmos" : "audio_truehd";
                case "eac3":
                    return atmos ? "audio_plus_atmos" : "audio_ddplus";
                case "ac3":
                    return "audio_dd";
                case "dts":
                case "dca":
                case "dtshd":
                    if (DtsXPattern.IsMatch(title)) return "audio_dtsx";
                    if (profile.Contains("dts-hd ma") || profile.Contains("dts-hd master")) return "audio_dtshdma";
                    return "audio_dts";
                default:
                    return null;
            }
        }

        /// <summary>
        /// Use the file name as a second signal: release names usually say "Atmos" or "DTS-X" even when the track
        /// title doesn't. Only upgrades a track that can carry the format.
        /// </summary>
        public static string UpgradeFromPath(string audio, string path)
        {
            if (audio == null || string.IsNullOrEmpty(path))
            {
                return audio;
            }

            var name = System.IO.Path.GetFileName(path);
            if (AtmosPattern.IsMatch(name))
            {
                if (audio == "audio_truehd") return "audio_truehd_atmos";
                if (audio == "audio_ddplus") return "audio_plus_atmos";
            }

            if ((audio == "audio_dtshdma" || audio == "audio_dts") && DtsXPattern.IsMatch(name))
            {
                return "audio_dtsx";
            }

            return audio;
        }

        /// <summary>Summarize many files (episodes) into one quality according to the rule.</summary>
        public static QualityInfo Summarize(IReadOnlyList<QualityInfo> files, bool mostCommon)
        {
            var nonEmpty = files.Where(f => f != null && !f.IsEmpty).ToList();
            if (nonEmpty.Count == 0)
            {
                return new QualityInfo();
            }

            return new QualityInfo
            {
                Resolution = Pick(nonEmpty.Select(f => f.Resolution), ResolutionRank, mostCommon),
                Hdr = Pick(nonEmpty.Select(f => f.Hdr), HdrRank, mostCommon),
                Audio = Pick(nonEmpty.Select(f => f.Audio), AudioRank, mostCommon),
            };
        }

        private static string Pick(IEnumerable<string> values, string[] rank, bool mostCommon)
        {
            var list = values.ToList();
            if (mostCommon)
            {
                // Most common value, including "none" (an SDR show with one HDR episode stays SDR). Ties go to the better one.
                return list
                    .GroupBy(v => v ?? string.Empty)
                    .OrderByDescending(g => g.Count())
                    .ThenByDescending(g => Array.IndexOf(rank, g.Key))
                    .Select(g => g.Key.Length == 0 ? null : g.Key)
                    .First();
            }

            return list.Where(v => v != null).OrderByDescending(v => Array.IndexOf(rank, v)).FirstOrDefault();
        }

        private static bool IsCoverArt(MediaStream s)
        {
            var codec = (s.Codec ?? string.Empty).ToLowerInvariant();
            return codec == "mjpeg" || codec == "png" || codec == "bmp";
        }
    }
}
