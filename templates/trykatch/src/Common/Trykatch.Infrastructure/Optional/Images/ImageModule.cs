using SkiaSharp;

namespace Trykatch.Infrastructure.Modules.Images;

public static class SafeImageProcessor
{
    public static byte[] ResizeAndStripMetadata(Stream input, int maximumEdge = 2048)
    {
        using SKBitmap source = SKBitmap.Decode(input) ?? throw new InvalidDataException("Unsupported image.");
        if (source.Width <= 0 || source.Height <= 0 || (long)source.Width * source.Height > 40_000_000)
        {
            throw new InvalidDataException("Image dimensions are unsafe.");
        }

        double scale = Math.Min(1d, (double)maximumEdge / Math.Max(source.Width, source.Height));
        SKImageInfo targetInfo = new(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
        using SKBitmap resized = source.Resize(targetInfo, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            ?? throw new InvalidDataException("Image could not be resized.");
        using SKImage image = SKImage.FromBitmap(resized);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Webp, 85);
        return encoded.ToArray();
    }
}
