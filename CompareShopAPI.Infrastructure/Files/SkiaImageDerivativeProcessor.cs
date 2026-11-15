using CompareShopAPI.Application.Files;
using CompareShopAPI.Domain.Files;
using SkiaSharp;
using System.Text;

namespace CompareShopAPI.Infrastructure.Files;

public sealed class SkiaImageDerivativeProcessor : IImageDerivativeProcessor
{
    private const long MaxPixels = 24_000_000;

    public string? DetectSourceContentType(ReadOnlyMemory<byte> source)
    {
        using var data = SKData.CreateCopy(source.Span);
        using var codec = SKCodec.Create(data);
        return codec?.EncodedFormat switch
        {
            SKEncodedImageFormat.Jpeg => "image/jpeg",
            SKEncodedImageFormat.Png => "image/png",
            SKEncodedImageFormat.Webp => "image/webp",
            SKEncodedImageFormat.Gif => "image/gif",
            _ => null
        };
    }

    public ProcessedDerivative Process(ReadOnlyMemory<byte> source, string requestedFormat, int width, int height,
        string fit, double focalX, double focalY, string background, int quality)
    {
        if (IsSvgOrPdf(source.Span)) throw new DerivativeFailure("unsupported_source_type");
        using var data = SKData.CreateCopy(source.Span);
        using var codec = SKCodec.Create(data) ?? throw new DerivativeFailure("decode_failed");
        if (codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > MaxPixels)
            throw new DerivativeFailure("source_pixel_limit_exceeded");
        var actual = codec.EncodedFormat;
        if (actual is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp or SKEncodedImageFormat.Gif))
            throw new DerivativeFailure("unsupported_source_type");
        if (source.Span.Length == 0) throw new DerivativeFailure("decode_failed");
        using var decoded = SKBitmap.Decode(data) ?? throw new DerivativeFailure("decode_failed");
        using var oriented = Orient(decoded, codec.EncodedOrigin);
        var warnings = new List<string>();
        if (actual == SKEncodedImageFormat.Gif) warnings.Add("gif_first_frame_only");
        var format = requestedFormat == "keep" ? actual switch
        {
            SKEncodedImageFormat.Jpeg => "jpg", SKEncodedImageFormat.Webp => "webp", _ => "png"
        } : requestedFormat;
        if (requestedFormat == "keep" && actual == SKEncodedImageFormat.Gif) warnings.Add("gif_keep_converted_to_png");
        var color = ParseBackground(background);
        using var output = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(output))
        {
            canvas.Clear(format == "jpg" && color.Alpha == 0 ? SKColors.White : color);
            var sourceRect = CalculateSourceRect(oriented.Width, oriented.Height, width, height, fit, focalX, focalY);
            var destinationRect = fit == "cover"
                ? new SKRect(0, 0, width, height)
                : ContainDestination(oriented.Width, oriented.Height, width, height);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            canvas.DrawBitmap(oriented, sourceRect, destinationRect, paint);
            canvas.Flush();
        }
        using var image = SKImage.FromBitmap(output);
        var encodedFormat = format switch
        {
            "jpg" => SKEncodedImageFormat.Jpeg, "webp" => SKEncodedImageFormat.Webp, "png" => SKEncodedImageFormat.Png,
            _ => throw new DerivativeFailure("invalid_variant")
        };
        using var encoded = image.Encode(encodedFormat, quality) ?? throw new DerivativeFailure("encode_failed");
        if (encoded.Size > FileUploadPolicy.MaxBytes) throw new DerivativeFailure("output_too_large");
        var outputType = format == "jpg" ? "image/jpeg" : $"image/{format}";
        return new(encoded.ToArray(), width, height, outputType, format, warnings);
    }

    private static bool IsSvgOrPdf(ReadOnlySpan<byte> source)
    {
        var header = source[..Math.Min(source.Length, 4096)];
        if (header.IndexOf("%PDF-"u8) >= 0) return true;
        var text = Encoding.UTF8.GetString(header).TrimStart('\uFEFF', '\u0000', ' ', '\t', '\r', '\n');
        return text.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ||
               (text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("<svg", StringComparison.OrdinalIgnoreCase));
    }

    internal static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        var swaps = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var result = new SKBitmap(swaps ? source.Height : source.Width, swaps ? source.Width : source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        var w = source.Width; var h = source.Height;
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: canvas.Translate(w, 0); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight: canvas.Translate(w, h); canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft: canvas.Translate(0, h); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop: canvas.RotateDegrees(90); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.RightTop: canvas.Translate(h, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: canvas.Translate(h, w); canvas.Scale(-1, 1); canvas.RotateDegrees(270); break;
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0, w); canvas.RotateDegrees(270); break;
        }
        canvas.DrawBitmap(source, 0, 0); canvas.Flush(); return result;
    }

    private static SKRect CalculateSourceRect(int sw, int sh, int tw, int th, string fit, double fx, double fy)
    {
        if (fit == "contain") return new(0, 0, sw, sh);
        var targetRatio = (double)tw / th; var sourceRatio = (double)sw / sh;
        var cw = sourceRatio > targetRatio ? (float)(sh * targetRatio) : sw;
        var ch = sourceRatio > targetRatio ? sh : (float)(sw / targetRatio);
        var left = Math.Clamp((float)(fx * sw - cw / 2), 0, sw - cw);
        var top = Math.Clamp((float)(fy * sh - ch / 2), 0, sh - ch);
        return new(left, top, left + cw, top + ch);
    }
    private static SKRect ContainDestination(int sw, int sh, int tw, int th)
    {
        var scale = Math.Min((double)tw / sw, (double)th / sh);
        var w = (float)(sw * scale); var h = (float)(sh * scale);
        return new((tw - w) / 2, (th - h) / 2, (tw + w) / 2, (th + h) / 2);
    }
    private static SKColor ParseBackground(string value)
    {
        if (value == "transparent") return SKColors.Transparent;
        return SKColor.TryParse(value, out var color) ? color : throw new DerivativeFailure("invalid_variant");
    }
}
