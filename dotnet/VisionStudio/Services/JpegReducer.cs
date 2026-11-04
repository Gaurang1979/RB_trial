using SkiaSharp;
using StationGuard;

namespace VisionStudio.Services;

/// <summary>
/// Reduces a saved NG image for the plant server: the full image stays on the line PC for training, the server
/// gets a JPEG of at most 300 KB (owner, 2026-10-30). Long side at most 1600 px, quality 80, 70, 60, 50, 40;
/// when even 40 is too big the size is cut to 0.75 and the qualities are tried again from 80.
/// </summary>
public static class JpegReducer
{
    /// <summary>The most the server takes for one image (300 KB).</summary>
    public const int MaxBytes = 307_200;
    public const int MaxLongSide = 1600;
    static readonly int[] Qualities = [80, 70, 60, 50, 40];

    /// <summary>
    /// <paramref name="gray"/> is width × height 8-bit pixels, top row first. Returns a grey JPEG of the same
    /// aspect, at most <paramref name="maxBytes"/> (a noise image never gets above it: the size keeps shrinking).
    /// </summary>
    public static byte[] Reduce(ReadOnlySpan<byte> gray, int width, int height, int maxBytes = MaxBytes, int maxLongSide = MaxLongSide)
    {
        if (width <= 0 || height <= 0 || gray.Length < (long)width * height) throw new ArgumentException("the pixels do not fill width × height");
        using var src = NewBitmap(width, height);
        unsafe
        {
            var dst = (byte*)src.GetPixels();
            for (var y = 0; y < height; y++) gray.Slice(y * width, width).CopyTo(new Span<byte>(dst + (long)y * src.RowBytes, width));
        }
        return Encode(src, maxBytes, maxLongSide);
    }

    /// <summary>
    /// <see cref="Reduce"/> for a BMP file the station saved, read row by row straight into the bitmap the encoder
    /// uses: one full-size copy in memory, not three. Throws <see cref="InvalidDataException"/> for another kind of BMP.
    /// </summary>
    public static byte[] ReduceBmp(string path, int maxBytes = MaxBytes, int maxLongSide = MaxLongSide)
    {
        using var r = BmpGray.Open(path);
        using var src = NewBitmap(r.Width, r.Height);
        var row = new byte[r.Width];
        unsafe
        {
            var dst = (byte*)src.GetPixels();
            int y;
            while ((y = r.ReadRow(row)) >= 0) row.CopyTo(new Span<byte>(dst + (long)y * src.RowBytes, r.Width));
        }
        return Encode(src, maxBytes, maxLongSide);
    }

    static SKBitmap NewBitmap(int width, int height) => new(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque));

    static byte[] Encode(SKBitmap src, int maxBytes, int maxLongSide)
    {
        int width = src.Width, height = src.Height;
        var scale = Math.Min(1.0, (double)maxLongSide / Math.Max(width, height));
        byte[] last = [];
        while (true)
        {
            int w = Math.Max(1, (int)Math.Round(width * scale)), h = Math.Max(1, (int)Math.Round(height * scale));
            using var small = w == width && h == height ? null : src.Resize(new SKImageInfo(w, h, SKColorType.Gray8, SKAlphaType.Opaque), SKFilterQuality.Medium);
            using var img = SKImage.FromBitmap(small ?? src);
            foreach (var q in Qualities)
            {
                using var data = img.Encode(SKEncodedImageFormat.Jpeg, q) ?? throw new InvalidOperationException("the JPEG encoder gave nothing");
                last = data.ToArray();
                if (last.Length <= maxBytes) return last;
            }
            // Cannot get smaller: only for an absurd budget; hand back the smallest made.
            if (w <= 8 || h <= 8) return last;
            scale *= 0.75;
        }
    }
}
