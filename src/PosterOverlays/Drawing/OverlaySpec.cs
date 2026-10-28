using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PosterOverlays.Data;
using PosterOverlays.Services;
using SkiaSharp;

namespace PosterOverlays.Drawing
{
    public enum PosterKind
    {
        Movie,
        Series,
        Season,
    }

    public sealed class BadgeSpec
    {
        /// <summary>Icon key in the icons folder, or null for text only.</summary>
        public string IconKey { get; set; }

        /// <summary>Text drawn when the icon is missing or icons are off (e.g. "4K", "IMDb").</summary>
        public string Label { get; set; }

        /// <summary>Value drawn after the icon or label (e.g. "8.4", "94%"), or null.</summary>
        public string Value { get; set; }

        /// <summary>Accent for text-only badges (e.g. tomato red).</summary>
        public SKColor? LabelColor { get; set; }
    }

    /// <summary>Everything the renderer needs. Built from cached data plus options; no Emby types.</summary>
    public sealed class OverlaySpec
    {
        public string BannerText { get; set; }

        public SKColor BannerColor { get; set; }

        public SKColor BannerTextColor { get; set; }

        public bool BannerAtTop { get; set; }

        public float BannerHeightFraction { get; set; }

        public List<BadgeSpec> Badges { get; } = new List<BadgeSpec>();

        public float BadgeHeightFraction { get; set; }

        public float BadgeMarginFraction { get; set; }

        public int MaxBadges { get; set; }

        public SKColor BadgeBackground { get; set; }

        public bool UseIcons { get; set; }

        public bool IsEmpty => string.IsNullOrEmpty(this.BannerText) && this.Badges.Count == 0;

        /// <summary>Content part of the image cache key: changes whenever what's drawn changes.</summary>
        public string ContentKey()
        {
            var sb = new StringBuilder();
            sb.Append(this.BannerText ?? "-");
            foreach (var b in this.Badges)
            {
                sb.Append('|').Append(b.IconKey ?? b.Label).Append(':').Append(b.Value);
            }

            return sb.ToString();
        }

        public static OverlaySpec Build(ItemOverlayData data, PosterKind kind, PluginOptions o)
        {
            var spec = new OverlaySpec
            {
                BannerAtTop = o.BannerPosition == BannerPosition.Top,
                BannerHeightFraction = o.BannerHeightPercent / 100f,
                BadgeHeightFraction = o.BadgeHeightPercent / 100f,
                BadgeMarginFraction = o.BadgeMarginPercent / 100f,
                MaxBadges = o.MaxBadges,
                BadgeBackground = Colors.Parse(o.BadgeBackgroundColor, SKColors.Black).WithAlpha((byte)(255 * o.BadgeBackgroundOpacity / 100)),
                BannerTextColor = Colors.Parse(o.BannerTextColor, SKColors.White),
                UseIcons = o.UseIcons,
            };

            if (data == null)
            {
                return spec;
            }

            if (kind != PosterKind.Movie && o.EnableStatusBanner)
            {
                (string text, string color) banner;
                switch (data.Status)
                {
                    case ShowStatus.Airing: banner = (o.AiringText, o.AiringColor); break;
                    case ShowStatus.Returning: banner = (o.ReturningText, o.ReturningColor); break;
                    case ShowStatus.Ended: banner = (o.EndedText, o.EndedColor); break;
                    case ShowStatus.Canceled: banner = (o.CanceledText, o.CanceledColor); break;
                    case ShowStatus.Upcoming: banner = o.ShowUpcoming ? (o.UpcomingText, o.UpcomingColor) : (null, null); break;
                    default: banner = (null, null); break;
                }

                if (!string.IsNullOrWhiteSpace(banner.text))
                {
                    spec.BannerText = banner.text.Trim();
                    spec.BannerColor = Colors.Parse(banner.color, SKColors.DimGray);
                }
            }

            var showRatings = kind != PosterKind.Season || o.ShowRatingsOnSeasons;
            foreach (var key in o.GetBadgeOrder())
            {
                BadgeSpec badge = null;
                switch (key)
                {
                    case "resolution":
                        if (o.ShowResolution && data.Resolution != null)
                        {
                            badge = new BadgeSpec { IconKey = data.Resolution, Label = Labels.For(data.Resolution) };
                        }

                        break;
                    case "hdr":
                        if (o.ShowHdr && data.Hdr != null)
                        {
                            badge = new BadgeSpec { IconKey = data.Hdr, Label = Labels.For(data.Hdr) };
                        }

                        break;
                    case "audio":
                        if (o.ShowAudio && data.Audio != null && (o.ShowBasicAudio || !MediaInfoClassifier.IsBasicAudio(data.Audio)))
                        {
                            badge = new BadgeSpec { IconKey = data.Audio, Label = Labels.For(data.Audio) };
                        }

                        break;
                    case "imdb":
                        if (showRatings && o.ShowImdb && data.Imdb.HasValue)
                        {
                            badge = new BadgeSpec
                            {
                                IconKey = "imdb",
                                Label = "IMDb",
                                Value = data.Imdb.Value.ToString("0.0", CultureInfo.InvariantCulture),
                                LabelColor = new SKColor(0xF5, 0xC5, 0x18),
                            };
                        }

                        break;
                    case "tmdb":
                        if (showRatings && o.ShowTmdb && data.Tmdb.HasValue)
                        {
                            badge = new BadgeSpec
                            {
                                IconKey = "tmdb",
                                Label = "TMDB",
                                Value = data.Tmdb.Value.ToString("0.0", CultureInfo.InvariantCulture),
                                LabelColor = new SKColor(0x01, 0xB4, 0xE4),
                            };
                        }

                        break;
                    case "rtcritic":
                        if (showRatings && o.ShowRtCritic && data.RtCritic.HasValue)
                        {
                            var fresh = data.RtCritic.Value >= 60;
                            badge = new BadgeSpec
                            {
                                IconKey = fresh ? "rt_critic_fresh" : "rt_critic_rotten",
                                Label = "RT",
                                Value = data.RtCritic.Value + "%",
                                LabelColor = fresh ? new SKColor(0xFA, 0x32, 0x0A) : new SKColor(0x8D, 0xB8, 0x3A),
                            };
                        }

                        break;
                    case "rtaudience":
                        if (showRatings && o.ShowRtAudience && data.RtAudience.HasValue)
                        {
                            var fresh = data.RtAudience.Value >= 60;
                            badge = new BadgeSpec
                            {
                                IconKey = fresh ? "rt_audience_fresh" : "rt_audience_rotten",
                                Label = "AUD",
                                Value = data.RtAudience.Value + "%",
                                LabelColor = fresh ? new SKColor(0xFA, 0x32, 0x0A) : new SKColor(0x8D, 0xB8, 0x3A),
                            };
                        }

                        break;
                }

                if (badge != null && spec.Badges.Count < o.MaxBadges)
                {
                    spec.Badges.Add(badge);
                }
            }

            return spec;
        }
    }

    public static class Labels
    {
        private static readonly Dictionary<string, string> Map = new Dictionary<string, string>
        {
            ["res_4k"] = "4K", ["res_1080p"] = "1080p", ["res_720p"] = "720p", ["res_576p"] = "576p", ["res_480p"] = "480p",
            ["hdr_dv"] = "DV", ["hdr_hdr10plus"] = "HDR10+", ["hdr_hdr10"] = "HDR10", ["hdr_hlg"] = "HLG",
            ["audio_truehd_atmos"] = "TrueHD Atmos", ["audio_plus_atmos"] = "DD+ Atmos", ["audio_atmos"] = "Atmos",
            ["audio_dtsx"] = "DTS:X", ["audio_dtshdma"] = "DTS-HD MA", ["audio_truehd"] = "TrueHD",
            ["audio_ddplus"] = "DD+", ["audio_dts"] = "DTS", ["audio_dd"] = "DD",
        };

        public static string For(string key) => key != null && Map.TryGetValue(key, out var v) ? v : key;

        public static IEnumerable<string> AllKeys => Map.Keys.Concat(new[] { "imdb" });
    }

    public static class Colors
    {
        public static bool TryParse(string value, out SKColor color)
        {
            color = SKColors.Transparent;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var v = value.Trim();
            if (!v.StartsWith("#"))
            {
                v = "#" + v;
            }

            return (v.Length == 7 || v.Length == 9) && SKColor.TryParse(v, out color);
        }

        public static SKColor Parse(string value, SKColor fallback) => TryParse(value, out var c) ? c : fallback;
    }
}
