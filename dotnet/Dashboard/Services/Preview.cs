using SkiaSharp;

namespace BarcodeReading.Dashboard.Services;

/// <summary>
/// Makes the live-view pictures from a frame shrunk to about 1200 px wide and brightened:
/// while decoding, a yellow box on every region the finder found; after decoding, a green
/// outline on every code read and a red box on every region that did not read, tagged with the
/// reason it did not read (blur, glare, too small...).
/// </summary>
public static class Preview
{
    public const int TargetWidth = 1200;

    /// <summary>Shrinks an 8-bit grey frame by area averaging; returns pixels and the scale used.</summary>
    public static unsafe (byte[] Pixels, int Width, int Height, int Factor) Shrink(IntPtr src, int width, int height, int stride)
    {
        int f = Math.Max(1, (int)Math.Ceiling(width / (double)TargetWidth));
        int w = width / f, h = height / f;
        var dst = new byte[w * h];
        byte* p = (byte*)src;
        int area = f * f;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int dy = 0; dy < f; dy++)
                {
                    byte* row = p + (long)(y * f + dy) * stride + x * f;
                    for (int dx = 0; dx < f; dx++) sum += row[dx];
                }
                dst[y * w + x] = (byte)(sum / area);
            }
        }
        return (dst, w, h, f);
    }

    /// <summary>JPEG of the shrunk frame with the found regions in yellow (decoding still running).</summary>
    public static byte[] RenderFound(byte[] gray, int w, int h, int factor, IReadOnlyList<FoundRegion> found, int quality = 75)
    {
        using var bmp = Brightened(gray, w, h);
        using var canvas = new SKCanvas(bmp);
        using var yellow = Pen(w, new SKColor(250, 210, 40));
        foreach (var g in found)
            canvas.DrawRect(g.Box.X / (float)factor, g.Box.Y / (float)factor, g.Box.Width / (float)factor, g.Box.Height / (float)factor, yellow);
        return Jpeg(bmp, quality);
    }

    /// <summary>
    /// JPEG of the shrunk frame with read codes (green), duplicate codes (orange, filled so they
    /// stand out) and unread regions (red) drawn on it.
    /// </summary>
    public static byte[] Render(byte[] gray, int w, int h, int factor, DecodeResult result,
                                IReadOnlyCollection<string>? duplicates = null, RoiSettings? roi = null, int quality = 75)
    {
        using var bmp = Brightened(gray, w, h);
        using var canvas = new SKCanvas(bmp);
        DrawCrop(canvas, w, h, factor, roi);
        using var green = Pen(w, new SKColor(40, 220, 90));
        using var red = Pen(w, new SKColor(240, 60, 50));
        using var orange = Pen(w, new SKColor(255, 140, 0));
        orange.StrokeWidth *= 1.5f;
        using var orangeFill = new SKPaint { Color = new SKColor(255, 140, 0, 90), Style = SKPaintStyle.Fill, IsAntialias = true };
        var dupSet = duplicates is null ? null : new HashSet<string>(duplicates, StringComparer.Ordinal);
        foreach (var code in result.Codes)
        {
            bool dup = dupSet?.Contains(code.Text) == true;
            using var path = new SKPath();
            path.MoveTo(code.Corners[0].X / factor, code.Corners[0].Y / factor);
            for (int k = 1; k < 4; k++) path.LineTo(code.Corners[k].X / factor, code.Corners[k].Y / factor);
            path.Close();
            if (dup) canvas.DrawPath(path, orangeFill);
            canvas.DrawPath(path, dup ? orange : green);
        }
        foreach (var m in result.Misses)
            canvas.DrawRect(m.X / (float)factor, m.Y / (float)factor, m.Width / (float)factor, m.Height / (float)factor, red);
        DrawMissTags(canvas, w, factor, result);
        return Jpeg(bmp, quality);
    }

    // the reason each unread region did not read, as a tag on its top edge (white on red)
    private static void DrawMissTags(SKCanvas canvas, int w, int factor, DecodeResult result)
    {
        if (result.MissInfo.Count == 0) return;
        using var text = new SKPaint { Color = SKColors.White, TextSize = Math.Max(11f, w / 90f), IsAntialias = true, Typeface = SKTypeface.FromFamilyName(null, SKFontStyle.Bold) };
        using var back = new SKPaint { Color = new SKColor(240, 60, 50, 220), Style = SKPaintStyle.Fill };
        foreach (var d in result.MissInfo)
        {
            if (!d.Measured && d.Slot is null) continue; // a slot is worth showing even undiagnosed
            string tag = d.Tag;
            float x = d.Box.X / (float)factor, y = d.Box.Y / (float)factor;
            float tw = text.MeasureText(tag), th = text.TextSize;
            canvas.DrawRect(x, Math.Max(0, y - th - 4), tw + 6, th + 4, back);
            canvas.DrawText(tag, x + 3, Math.Max(th, y - 4), text);
        }
    }

    /// <summary>
    /// JPEG for calibration: read codes green, unread regions red, and the proposed crop in blue
    /// with the part outside it dimmed (that part is not processed).
    /// </summary>
    public static byte[] RenderCalibration(byte[] gray, int w, int h, int factor, DecodeResult result, RoiSettings? roi, int quality = 80)
    {
        using var bmp = Brightened(gray, w, h);
        using var canvas = new SKCanvas(bmp);
        DrawCrop(canvas, w, h, factor, roi);
        using var green = Pen(w, new SKColor(40, 220, 90));
        using var red = Pen(w, new SKColor(240, 60, 50));
        foreach (var code in result.Codes)
        {
            using var path = new SKPath();
            path.MoveTo(code.Corners[0].X / factor, code.Corners[0].Y / factor);
            for (int k = 1; k < 4; k++) path.LineTo(code.Corners[k].X / factor, code.Corners[k].Y / factor);
            path.Close();
            canvas.DrawPath(path, green);
        }
        foreach (var m in result.Misses)
            canvas.DrawRect(m.X / (float)factor, m.Y / (float)factor, m.Width / (float)factor, m.Height / (float)factor, red);
        return Jpeg(bmp, quality);
    }

    // the crop in blue, the part outside it (not processed) dimmed
    private static void DrawCrop(SKCanvas canvas, int w, int h, int factor, RoiSettings? roi)
    {
        if (roi is null) return;
        var r = new SKRect(roi.X / (float)factor, roi.Y / (float)factor, (roi.X + roi.Width) / (float)factor, (roi.Y + roi.Height) / (float)factor);
        using var dim = new SKPaint { Color = new SKColor(0, 0, 0, 150), Style = SKPaintStyle.Fill };
        canvas.Save();
        canvas.ClipRect(r, SKClipOperation.Difference);
        canvas.DrawRect(0, 0, w, h, dim);
        canvas.Restore();
        using var blue = Pen(w, new SKColor(60, 160, 255));
        blue.StrokeWidth *= 1.5f;
        canvas.DrawRect(r, blue);
    }

    private static SKPaint Pen(int w, SKColor color) =>
        new() { Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(2f, w / 400f), IsAntialias = true };

    private static byte[] Jpeg(SKBitmap bmp, int quality)
    {
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    private static SKBitmap Brightened(byte[] gray, int w, int h)
    {
        // brighten like the engine does (1st..99.5th percentile -> 0..255) so dark lines are visible
        var hist = new int[256];
        foreach (var b in gray) hist[b]++;
        int lo = Percentile(hist, gray.Length, 0.01), hi = Percentile(hist, gray.Length, 0.995);
        if (hi - lo < 4) hi = lo + 4;
        var lut = new byte[256];
        for (int i = 0; i < 256; i++) lut[i] = (byte)Math.Clamp((i - lo) * 255 / (hi - lo), 0, 255);

        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var px = new uint[w * h];
        for (int i = 0; i < px.Length; i++)
        {
            uint v = lut[gray[i]];
            px[i] = 0xFF000000u | (v << 16) | (v << 8) | v;
        }
        unsafe
        {
            fixed (uint* s = px)
                Buffer.MemoryCopy(s, (void*)bmp.GetPixels(), (long)px.Length * 4, (long)px.Length * 4);
        }

        return bmp;
    }

    private static int Percentile(int[] hist, int n, double q)
    {
        long target = (long)(q * (n - 1)), acc = 0;
        for (int v = 0; v < 256; v++)
        {
            acc += hist[v];
            if (acc > target) return v;
        }
        return 255;
    }
}
