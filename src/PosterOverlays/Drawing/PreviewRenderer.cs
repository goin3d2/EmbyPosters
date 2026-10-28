using System;
using PosterOverlays.Data;
using SkiaSharp;

namespace PosterOverlays.Drawing
{
    /// <summary>Renders the settings page previews with the same code the server uses for real posters.</summary>
    public static class PreviewRenderer
    {
        public const int PreviewWidth = 500;

        /// <summary>Data that exercises every badge, so each setting has something visible to change.</summary>
        public static ItemOverlayData SampleData(ShowStatus status) => new ItemOverlayData
        {
            Status = status,
            Resolution = "res_4k",
            Hdr = "hdr_dv",
            Audio = "audio_truehd_atmos",
            Imdb = 8.4,
            RtCritic = 94,
            RtAudience = 88,
            Tmdb = 8.1,
        };

        /// <param name="source">The poster to draw on, or null for a placeholder.</param>
        public static byte[] RenderPng(SKBitmap source, PosterKind kind, PluginOptions options, ItemOverlayData data, Func<string, SKImage> icons)
        {
            var spec = OverlaySpec.Build(data, kind, options);
            using var placeholder = source == null ? Placeholder(kind) : null;
            var poster = source ?? placeholder;

            var width = Math.Min(PreviewWidth, poster.Width);
            var height = (int)Math.Round(poster.Height * (width / (double)poster.Width));
            using var image = OverlayRenderer.RenderImage(poster, width, height, spec, icons);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            return png.ToArray();
        }

        /// <summary>A neutral 2:3 poster with soft shapes and the kind written faintly in the middle.</summary>
        public static SKBitmap Placeholder(PosterKind kind)
        {
            const int w = 600, h = 900;
            var (top, bottom) = kind switch
            {
                PosterKind.Movie => (new SKColor(0x2E, 0x3A, 0x59), new SKColor(0x0E, 0x12, 0x1C)),
                PosterKind.Series => (new SKColor(0x3B, 0x2E, 0x55), new SKColor(0x12, 0x0E, 0x1C)),
                _ => (new SKColor(0x2B, 0x47, 0x45), new SKColor(0x0C, 0x16, 0x15)),
            };

            var bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            using (var gradient = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, h), new[] { top, bottom }, SKShaderTileMode.Clamp),
            })
            {
                canvas.DrawRect(0, 0, w, h, gradient);
            }

            var random = new Random((int)kind + 7);
            using (var blob = new SKPaint { IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 18) })
            {
                for (var i = 0; i < 9; i++)
                {
                    blob.Color = SKColors.White.WithAlpha((byte)random.Next(10, 28));
                    canvas.DrawCircle(random.Next(0, w), random.Next(0, h), random.Next(50, 160), blob);
                }
            }

            using (var text = new SKPaint
            {
                Typeface = OverlayRenderer.Font,
                IsAntialias = true,
                Color = SKColors.White.WithAlpha(90),
                TextSize = 44,
                TextAlign = SKTextAlign.Center,
            })
            {
                canvas.DrawText(kind.ToString().ToUpperInvariant(), w / 2f, h * 0.42f, text);
            }

            return bitmap;
        }
    }
}
