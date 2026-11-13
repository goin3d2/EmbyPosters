using System;
using System.IO;
using SkiaSharp;

namespace PosterOverlays.Drawing
{
    /// <summary>Draws a banner and badge stack onto a poster. Pure Skia: no Emby types, safe to call from many threads.</summary>
    public static class OverlayRenderer
    {
        private static readonly Lazy<SKTypeface> EmbeddedFont = new Lazy<SKTypeface>(() => LoadFont("PosterOverlays.OverlayFont.ttf"));

        private static readonly Lazy<SKTypeface> EmbeddedWideFont = new Lazy<SKTypeface>(() => LoadFont("PosterOverlays.OverlayFontWide.ttf"));

        public static SKTypeface Font => EmbeddedFont.Value;

        public static SKTypeface WideFont => EmbeddedWideFont.Value;

        private static SKTypeface FontFor(OverlaySpec spec) => spec.WideFont ? WideFont : Font;

        private static SKTypeface LoadFont(string resource)
        {
            var stream = typeof(OverlayRenderer).Assembly.GetManifestResourceStream(resource);
            if (stream == null)
            {
                return SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Bold);
            }

            using (stream)
            {
                var copy = new MemoryStream();
                stream.CopyTo(copy);
                copy.Position = 0;
                return SKTypeface.FromStream(copy) ?? SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Bold);
            }
        }

        /// <summary>Decode inputFile, draw the overlay, and write outputFile in a format matching its extension.</summary>
        public static void RenderFile(string inputFile, string outputFile, OverlaySpec spec, Func<string, SKImage> icons)
        {
            using var source = SKBitmap.Decode(inputFile) ?? throw new InvalidDataException("Could not decode " + inputFile);
            using var image = RenderImage(source, source.Width, source.Height, spec, icons);
            var (format, quality) = FormatFor(outputFile);
            using var data = image.Encode(format, quality) ?? image.Encode(SKEncodedImageFormat.Png, 100);
            Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? ".");
            using var output = File.Create(outputFile);
            data.SaveTo(output);
        }

        /// <summary>Draw source scaled to width x height, then the overlay on top.</summary>
        public static SKImage RenderImage(SKBitmap source, int width, int height, OverlaySpec spec, Func<string, SKImage> icons)
        {
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info) ?? throw new InvalidOperationException("Could not create a drawing surface");
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            using (var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High })
            {
                canvas.DrawBitmap(source, new SKRect(0, 0, width, height), paint);
            }

            Render(canvas, width, height, spec, icons);
            canvas.Flush();
            return surface.Snapshot();
        }

        public static void Render(SKCanvas canvas, int width, int height, OverlaySpec spec, Func<string, SKImage> icons)
        {
            if (spec == null || spec.IsEmpty || width < 50 || height < 50)
            {
                return;
            }

            float bannerTop = 0, bannerBottom = 0;
            var hasBanner = !string.IsNullOrEmpty(spec.BannerText);
            if (hasBanner)
            {
                var bh = (float)Math.Round(height * spec.BannerHeightFraction);
                bannerTop = spec.BannerAtTop ? 0 : height - bh;
                bannerBottom = bannerTop + bh;
                DrawBanner(canvas, new SKRect(0, bannerTop, width, bannerBottom), spec);
            }

            if (spec.Badges.Count == 0)
            {
                return;
            }

            var margin = width * spec.BadgeMarginFraction;
            var badgeHeight = Math.Max(14f, height * spec.BadgeHeightFraction);
            var gap = badgeHeight * 0.22f;
            var y = hasBanner && spec.BannerAtTop ? bannerBottom + margin : margin;
            var limit = hasBanner && !spec.BannerAtTop ? bannerTop - margin : height - margin;
            var maxWidth = width * 0.6f;

            if (spec.Column)
            {
                var areaTop = hasBanner && spec.BannerAtTop ? bannerBottom : 0;
                var areaBottom = hasBanner && !spec.BannerAtTop ? bannerTop : height;
                DrawColumn(canvas, width, margin, y, limit, areaTop, areaBottom, badgeHeight, spec, icons);
                return;
            }
            var drawn = 0;

            foreach (var badge in spec.Badges)
            {
                if (drawn >= spec.MaxBadges || y + (badgeHeight * Math.Max(1f, spec.IconScale)) > limit)
                {
                    break;
                }

                var used = DrawBadge(canvas, margin, y, badgeHeight, maxWidth, badge, spec, spec.UseIcons ? icons?.Invoke(badge.IconKey) : null);
                y += used + gap;
                drawn++;
            }
        }

        private static void DrawBanner(SKCanvas canvas, SKRect rect, OverlaySpec spec)
        {
            using (var fill = new SKPaint { Color = spec.BannerColor, IsAntialias = true, Style = SKPaintStyle.Fill })
            {
                canvas.DrawRect(rect, fill);
            }

            // Hairline on the poster-facing edge so a solid banner reads as a band, not a crop.
            // A see-through banner already reads as an overlay, so it gets none.
            if (spec.BannerColor.Alpha == 255)
            {
                using var edge = new SKPaint { Color = SKColors.Black.WithAlpha(70), IsAntialias = true, StrokeWidth = Math.Max(1f, rect.Height * 0.04f) };
                var edgeY = spec.BannerAtTop ? rect.Bottom : rect.Top;
                canvas.DrawLine(rect.Left, edgeY, rect.Right, edgeY, edge);
            }

            var text = spec.BannerText.ToUpperInvariant();
            using var paint = new SKPaint
            {
                Typeface = FontFor(spec),
                IsAntialias = true,
                Color = spec.BannerTextColor,
                TextSize = rect.Height * (spec.WideFont ? 0.68f : 0.56f),
            };

            // Letter-spaced, shrunk to fit 90% of the width.
            var tracking = paint.TextSize * spec.BannerTracking;
            var textWidth = MeasureTracked(paint, text, tracking);
            var available = rect.Width * 0.9f;
            if (textWidth > available)
            {
                var scale = available / textWidth;
                paint.TextSize *= scale;
                tracking *= scale;
                textWidth = MeasureTracked(paint, text, tracking);
            }

            var x = rect.MidX - (textWidth / 2);
            var baseline = rect.MidY + (CapHeight(paint) / 2);
            foreach (var ch in text)
            {
                var s = ch.ToString();
                canvas.DrawText(s, x, baseline, paint);
                x += paint.MeasureText(s) + tracking;
            }
        }

        /// <returns>The height actually drawn (taller than h when a large icon size needs the room).</returns>
        private static float DrawBadge(SKCanvas canvas, float x, float y, float h, float maxWidth, BadgeSpec badge, OverlaySpec spec, SKImage icon)
        {
            var pad = h * 0.2f;
            var inner = h - (2 * pad);
            using var text = new SKPaint { Typeface = FontFor(spec), IsAntialias = true, Color = SKColors.White, TextSize = h * 0.56f };

            // Measure contents: [icon or label] [value]
            float iconW = 0, iconH = 0;
            if (icon != null)
            {
                // Icon-only badges (resolution, HDR, audio) get a bit less height so wide logos stay compact.
                iconH = (badge.Value == null ? inner * 0.92f : inner * 1.08f) * spec.IconScale;
                iconW = icon.Width * (iconH / icon.Height);
                var iconMax = maxWidth - (2 * pad) - (badge.Value != null ? text.MeasureText(badge.Value) + pad : 0);
                if (iconW > iconMax && iconMax > 0)
                {
                    iconH *= iconMax / iconW;
                    iconW = iconMax;
                }
            }

            var label = icon == null ? badge.Label : null;
            var labelW = label != null ? text.MeasureText(label) : 0;
            var valueW = badge.Value != null ? text.MeasureText(badge.Value) : 0;
            var spacing = pad * 0.8f;
            var contentW = iconW + labelW + valueW + ((iconW + labelW) > 0 && valueW > 0 ? spacing : 0);
            var badgeW = Math.Min(maxWidth, contentW + (2 * pad));

            var pillH = Math.Max(h, iconH + pad);
            var rect = new SKRect(x, y, x + badgeW, y + pillH);
            using (var bg = new SKPaint { Color = spec.BadgeBackground, IsAntialias = true })
            {
                canvas.DrawRoundRect(rect, h * 0.22f, h * 0.22f, bg);
            }

            var cx = x + pad;
            var baseline = rect.MidY + (CapHeight(text) / 2);
            if (icon != null)
            {
                var dest = SKRect.Create(cx, rect.MidY - (iconH / 2), iconW, iconH);
                using var imagePaint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
                canvas.DrawImage(icon, dest, imagePaint);
                cx += iconW;
            }
            else if (label != null)
            {
                var color = text.Color;
                text.Color = badge.LabelColor ?? SKColors.White;
                canvas.DrawText(label, cx, baseline, text);
                text.Color = color;
                cx += labelW;
            }

            if (badge.Value != null)
            {
                cx += (iconW + labelW) > 0 ? spacing : 0;
                canvas.DrawText(badge.Value, cx, baseline, text);
            }

            return pillH;
        }

        /// <summary>
        /// One panel down the left edge: each badge's icon (or label) with its value centered underneath.
        /// Edges that sit against the poster edge or the banner are square; the free corners are rounded.
        /// </summary>
        private static void DrawColumn(SKCanvas canvas, int width, float margin, float top, float limit, float areaTop, float areaBottom, float unit, OverlaySpec spec, Func<string, SKImage> icons)
        {
            var pad = unit * 0.28f;
            var itemGap = unit * 0.32f;
            var valueGap = unit * 0.1f;
            var maxInner = width * 0.2f;
            using var text = new SKPaint { Typeface = FontFor(spec), IsAntialias = true, Color = SKColors.White, TextSize = unit * 0.62f };
            var cap = CapHeight(text);

            // Lay out items until the column would run past the available space.
            var items = new System.Collections.Generic.List<(BadgeSpec Badge, SKImage Icon, float IconW, float IconH, float W, float H)>();
            var used = 2 * pad;
            foreach (var badge in spec.Badges)
            {
                if (items.Count >= spec.MaxBadges)
                {
                    break;
                }

                var icon = spec.UseIcons ? icons?.Invoke(badge.IconKey) : null;
                float iconW = 0, iconH = 0, headH, headW;
                if (icon != null)
                {
                    iconH = unit * (badge.Value != null ? 0.58f : 0.62f) * spec.IconScale;
                    iconW = icon.Width * (iconH / icon.Height);
                    if (iconW > maxInner)
                    {
                        iconH *= maxInner / iconW;
                        iconW = maxInner;
                    }

                    headW = iconW;
                    headH = iconH;
                }
                else
                {
                    headW = Math.Min(maxInner, text.MeasureText(badge.Label ?? string.Empty));
                    headH = cap;
                }

                var valueW = badge.Value != null ? text.MeasureText(badge.Value) : 0;
                var h = headH + (badge.Value != null ? valueGap + cap : 0);
                var extra = h + (items.Count > 0 ? itemGap : 0);
                if (top + used + extra > limit)
                {
                    break;
                }

                used += extra;
                items.Add((badge, icon, iconW, iconH, Math.Max(headW, valueW), h));
            }

            if (items.Count == 0)
            {
                return;
            }

            var innerW = 0f;
            foreach (var item in items)
            {
                innerW = Math.Max(innerW, item.W);
            }

            var panel = new SKRect(margin, top, margin + innerW + (2 * pad), top + used);
            var radius = unit * 0.3f;
            using (var bg = new SKPaint { Color = spec.BadgeBackground, IsAntialias = true })
            {
                // Push edges that touch the poster edge or banner out by the radius, then clip, so they stay square.
                var draw = panel;
                if (margin < 0.5f)
                {
                    draw.Left -= radius;
                }

                if (top - areaTop < 0.5f)
                {
                    draw.Top -= radius;
                }

                canvas.Save();
                canvas.ClipRect(new SKRect(0, areaTop, width, areaBottom));
                canvas.DrawRoundRect(draw, radius, radius, bg);
                canvas.Restore();
            }

            var y = top + pad;
            var centerX = panel.MidX;
            foreach (var item in items)
            {
                if (item.Icon != null)
                {
                    var dest = SKRect.Create(centerX - (item.IconW / 2), y, item.IconW, item.IconH);
                    using var imagePaint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
                    canvas.DrawImage(item.Icon, dest, imagePaint);
                    y += item.IconH;
                }
                else
                {
                    var color = text.Color;
                    text.Color = item.Badge.LabelColor ?? SKColors.White;
                    var label = item.Badge.Label ?? string.Empty;
                    canvas.DrawText(label, centerX - (text.MeasureText(label) / 2), y + cap, text);
                    text.Color = color;
                    y += cap;
                }

                if (item.Badge.Value != null)
                {
                    y += valueGap;
                    canvas.DrawText(item.Badge.Value, centerX - (text.MeasureText(item.Badge.Value) / 2), y + cap, text);
                    y += cap;
                }

                y += itemGap;
            }
        }

        private static float MeasureTracked(SKPaint paint, string text, float tracking)
        {
            float w = 0;
            foreach (var ch in text)
            {
                w += paint.MeasureText(ch.ToString()) + tracking;
            }

            return text.Length > 0 ? w - tracking : 0;
        }

        private static float CapHeight(SKPaint paint)
        {
            var cap = paint.FontMetrics.CapHeight;
            return cap > 0 ? cap : paint.TextSize * 0.7f;
        }

        private static (SKEncodedImageFormat, int) FormatFor(string path)
        {
            switch (Path.GetExtension(path)?.ToLowerInvariant())
            {
                case ".png":
                    return (SKEncodedImageFormat.Png, 100);
                case ".webp":
                    return (SKEncodedImageFormat.Webp, 90);
                default:
                    return (SKEncodedImageFormat.Jpeg, 92);
            }
        }
    }
}
