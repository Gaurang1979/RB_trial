using SkiaSharp;
using VisionCore;

namespace VisionStudio.Services;

/// <summary>Draws an image with the tools' results over it and encodes it as JPEG for the browser.</summary>
public static class Render
{
    public const int MaxWidth = 960;

    public sealed record Frame(byte[] Jpeg, int Width, int Height, double Scale);

    public static Frame Draw(GrayImage img, IEnumerable<Graphic>? graphics, IEnumerable<RoiBox>? rois = null, int maxWidth = MaxWidth)
    {
        double s = Math.Min(1.0, (double)maxWidth / img.Width);
        int w = Math.Max(1, (int)Math.Round(img.Width * s)), h = Math.Max(1, (int)Math.Round(img.Height * s));
        using var src = new SKBitmap(new SKImageInfo(img.Width, img.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        unsafe
        {
            byte* dst = (byte*)src.GetPixels();
            for (int y = 0; y < img.Height; y++)
                img.Pixels.AsSpan(y * img.Stride, img.Width).CopyTo(new Span<byte>(dst + (long)y * src.RowBytes, img.Width));
        }
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        using (var scaled = src.Resize(new SKImageInfo(w, h, SKColorType.Gray8, SKAlphaType.Opaque), SKFilterQuality.Medium))
            c.DrawBitmap(scaled, 0, 0);
        using var roiPaint = new SKPaint { Color = new SKColor(80, 150, 255), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new[] { 8f, 6f }, 0) };
        foreach (var r in rois ?? Array.Empty<RoiBox>())
            c.DrawRect((float)(r.X * s), (float)(r.Y * s), (float)(r.Width * s), (float)(r.Height * s), roiPaint);
        using var font = new SKFont(SKTypeface.Default, 15);
        foreach (var g in graphics ?? Array.Empty<Graphic>())
        {
            var color = g.Kind switch
            {
                GraphicKind.Ok => new SKColor(40, 200, 90),
                GraphicKind.Fail => new SKColor(240, 60, 50),
                _ => new SKColor(250, 200, 40),
            };
            using var p = new SKPaint { Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, IsAntialias = true };
            using var path = new SKPath();
            for (int i = 0; i < g.Points.Length; i++)
            {
                var pt = new SKPoint((float)(g.Points[i].X * s), (float)(g.Points[i].Y * s));
                if (i == 0) path.MoveTo(pt); else path.LineTo(pt);
            }
            path.Close();
            c.DrawPath(path, p);
        }
        using var snap = surface.Snapshot();
        using var data = snap.Encode(SKEncodedImageFormat.Jpeg, 82);
        return new Frame(data.ToArray(), w, h, s);
    }
}

/// <summary>The latest rendered images by key ("live0", "teach-<id>"), served at /img/{key}.jpg.</summary>
public sealed class ImageCache
{
    /// <summary>More keys than this (pages left open, never closed) and the oldest non-live ones go.</summary>
    public const int MaxKeys = 64;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (Render.Frame Frame, long Order)> _frames = new();
    private long _order;

    public void Put(string key, Render.Frame f)
    {
        _frames[key] = (f, Interlocked.Increment(ref _order));
        if (_frames.Count > MaxKeys)
            foreach (var old in _frames.Where(kv => !kv.Key.StartsWith("live")).OrderBy(kv => kv.Value.Order).Take(_frames.Count - MaxKeys).ToList())
                _frames.TryRemove(old.Key, out _);
    }

    public Render.Frame? Get(string key) => _frames.TryGetValue(key, out var f) ? f.Frame : null;

    /// <summary>A page that put images here removes them when it closes.</summary>
    public void Remove(string key) => _frames.TryRemove(key, out _);

    public int Count => _frames.Count;

    /// <summary>A clean-up step for the resource guard: drops every image but the live ones; returns the bytes freed.</summary>
    public long TrimNonLive()
    {
        long freed = 0;
        foreach (var kv in _frames.Where(kv => !kv.Key.StartsWith("live")).ToList())
            if (_frames.TryRemove(kv.Key, out var v)) freed += v.Frame.Jpeg.Length;
        return freed;
    }
}
