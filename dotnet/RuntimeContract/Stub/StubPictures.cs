using System.Buffers.Binary;
using System.IO.Compression;

namespace VisionStudio.Remote.Stub;

/// <summary>
/// Drawn pictures for the stub runtime, so GetImage has something to send without customer
/// images: a plain picture of the case with its codes as dark squares, and a product picture
/// per job. Written as PNG with no image library.
/// </summary>
public static class StubPictures
{
    /// <summary>The case as the camera might see it: a light carton with a dark square for each read
    /// code and an empty light square where one was missed.</summary>
    public static byte[] Case(int width, int height, IReadOnlyList<(float X0, float Y0, float X1, float Y1, bool Missed)> codes)
    {
        var pixels = new byte[width * height * 3];
        Fill(pixels, width, 0, 0, width, height, (58, 62, 68));
        Fill(pixels, width, width / 20, height / 20, width - width / 20, height - height / 20, (196, 178, 140));
        foreach (var (x0, y0, x1, y1, missed) in codes)
        {
            var (a, b, c, d) = ((int)x0, (int)y0, (int)x1, (int)y1);
            Fill(pixels, width, a - 6, b - 6, c + 6, d + 6, (238, 234, 224));   // the label
            if (missed) continue;
            // a rough module pattern, the same for every code
            var cell = Math.Max(2, (c - a) / 12);
            for (var y = b; y < d; y += cell)
                for (var x = a; x < c; x += cell)
                    if (((x - a) / cell * 7 + (y - b) / cell * 3) % 5 < 2 || x == a || y + cell >= d)
                        Fill(pixels, width, x, y, Math.Min(c, x + cell), Math.Min(d, y + cell), (24, 24, 24));
        }
        return Png(width, height, pixels);
    }

    /// <summary>A product picture for job number <paramref name="job"/>: a bottle, a blister or a label.</summary>
    public static byte[] Product(int job, int width = 320, int height = 240)
    {
        var pixels = new byte[width * height * 3];
        (byte, byte, byte) back = (job % 3) switch { 1 => (92, 54, 24), 2 => (34, 84, 120), _ => (60, 92, 60) };
        (byte, byte, byte) fore = (job % 3) switch { 1 => (214, 160, 72), 2 => (226, 230, 236), _ => (240, 236, 220) };
        Fill(pixels, width, 0, 0, width, height, back);
        int cx = width / 2, cy = height / 2;
        switch (job % 3)
        {
            case 1:   // bottle: body, shoulder, neck, label
                Fill(pixels, width, cx - 40, cy - 40, cx + 40, cy + 100, fore);
                Fill(pixels, width, cx - 28, cy - 60, cx + 28, cy - 40, fore);
                Fill(pixels, width, cx - 12, cy - 105, cx + 12, cy - 60, fore);
                Fill(pixels, width, cx - 32, cy + 5, cx + 32, cy + 55, (240, 236, 220));
                break;
            case 2:   // blister of 2 x 5 tablets
                Fill(pixels, width, cx - 120, cy - 60, cx + 120, cy + 60, (180, 186, 196));
                for (var row = 0; row < 2; row++)
                    for (var col = 0; col < 5; col++)
                        Disc(pixels, width, cx - 96 + col * 48, cy - 26 + row * 52, 18, fore);
                break;
            default:  // label with text lines
                Fill(pixels, width, cx - 110, cy - 70, cx + 110, cy + 70, fore);
                for (var line = 0; line < 4; line++)
                    Fill(pixels, width, cx - 90, cy - 50 + line * 28, cx + 90 - line * 30, cy - 38 + line * 28, (60, 60, 60));
                break;
        }
        return Png(width, height, pixels);
    }

    private static void Fill(byte[] p, int width, int x0, int y0, int x1, int y1, (byte R, byte G, byte B) c)
    {
        var height = p.Length / 3 / width;
        x0 = Math.Clamp(x0, 0, width); x1 = Math.Clamp(x1, 0, width);
        y0 = Math.Clamp(y0, 0, height); y1 = Math.Clamp(y1, 0, height);
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                var i = (y * width + x) * 3;
                (p[i], p[i + 1], p[i + 2]) = c;
            }
    }

    private static void Disc(byte[] p, int width, int cx, int cy, int r, (byte R, byte G, byte B) c)
    {
        for (var y = -r; y <= r; y++)
        {
            var half = (int)Math.Sqrt(r * r - y * y);
            Fill(p, width, cx - half, cy + y, cx + half + 1, cy + y + 1, c);
        }
    }

    /// <summary>An 8-bit RGB PNG.</summary>
    public static byte[] Png(int width, int height, byte[] rgb)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (var y = 0; y < height; y++)
            {
                z.WriteByte(0);   // no filter
                z.Write(rgb, y * width * 3, width * 3);
            }
        }
        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;   // bits per sample
        header[9] = 2;   // RGB
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", raw.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(four, data.Length);
        s.Write(four);
        var typed = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type, typed);
        data.CopyTo(typed, 4);
        s.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(four, Crc(typed));
        s.Write(four);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
