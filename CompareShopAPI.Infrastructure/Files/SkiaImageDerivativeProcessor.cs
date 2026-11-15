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
        if (IsPdfHeader(source)) return true;

        var text = Encoding.UTF8.GetString(source[..Math.Min(source.Length, 4096)])
            .TrimStart('\uFEFF', '\u0000', ' ', '\t', '\r', '\n');
        return StartsWithSvgElement(text);
    }

    private static bool IsPdfHeader(ReadOnlySpan<byte> source)
    {
        // PDF readers allow a short preamble, but the header must remain at the start of the file.
        var limit = Math.Min(source.Length, 1024);
        var hasBom = limit >= 3 && source[0] == 0xEF && source[1] == 0xBB && source[2] == 0xBF;
        var index = hasBom ? 3 : 0;
        while (index < limit && source[index] is 0x09 or 0x0A or 0x0C or 0x0D or 0x20) index++;
        return source[index..limit].StartsWith("%PDF-"u8);
    }

    private static bool StartsWithSvgElement(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            if (text.AsSpan(index).StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
            {
                var end = text.IndexOf("?>", index, StringComparison.Ordinal);
                if (end < 0) return false;
                index = end + 2;
                continue;
            }
            if (text.AsSpan(index).StartsWith("<!--", StringComparison.Ordinal))
            {
                var end = text.IndexOf("-->", index + 4, StringComparison.Ordinal);
                if (end < 0) return false;
                index = end + 3;
                continue;
            }
            if (text.AsSpan(index).StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
            {
                var end = FindDoctypeEnd(text, index + 9);
                if (end < 0) return false;
                index = end + 1;
                continue;
            }

            if (index >= text.Length || text[index] != '<' || index + 4 > text.Length ||
                !text.AsSpan(index + 1).StartsWith("svg", StringComparison.OrdinalIgnoreCase)) return false;
            var next = index + 4;
            return next < text.Length && (char.IsWhiteSpace(text[next]) || text[next] is '>' or '/');
        }
        return false;
    }

    private static int FindDoctypeEnd(string text, int index)
    {
        var subsetDepth = 0;
        var quote = '\0';
        for (; index < text.Length; index++)
        {
            var current = text[index];
            if (quote != '\0')
            {
                if (current == quote) quote = '\0';
                continue;
            }
            if (current is '\'' or '"') quote = current;
            else if (current == '[') subsetDepth++;
            else if (current == ']') subsetDepth = Math.Max(0, subsetDepth - 1);
            else if (current == '>' && subsetDepth == 0) return index;
        }
        return -1;
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
