using System;
using System.IO;

namespace BarcodeReading;

/// <summary>
/// Minimal reader for uncompressed 8-bit (palette) and 24-bit BMP files, as saved by industrial
/// camera tools and by the line's hard-case folder. Returns top-down 8-bit grey pixels with
/// stride = width.
/// </summary>
public static class BmpImage
{
    public static (byte[] Pixels, int Width, int Height) LoadGray(string path) => DecodeGray(File.ReadAllBytes(path));

    /// <summary>Writes 8-bit grey pixels (stride = width) as an uncompressed 8-bit palette BMP.</summary>
    public static void SaveGray(string path, ReadOnlySpan<byte> pixels, int width, int height)
    {
        var data = EncodeGray(pixels, width, height);
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>8-bit grey pixels (stride = width) as the bytes of an uncompressed 8-bit palette BMP.</summary>
    public static byte[] EncodeGray(ReadOnlySpan<byte> pixels, int width, int height)
    {
        if (pixels.Length < width * height) throw new ArgumentException("pixel buffer too small");
        int rowBytes = (width + 3) & ~3;
        int offset = 14 + 40 + 256 * 4;
        int size = offset + rowBytes * height;
        var data = new byte[size];
        data[0] = (byte)'B';
        data[1] = (byte)'M';
        BitConverter.GetBytes(size).CopyTo(data, 2);
        BitConverter.GetBytes(offset).CopyTo(data, 10);
        BitConverter.GetBytes(40).CopyTo(data, 14);
        BitConverter.GetBytes(width).CopyTo(data, 18);
        BitConverter.GetBytes(height).CopyTo(data, 22);          // positive = bottom-up
        BitConverter.GetBytes((short)1).CopyTo(data, 26);
        BitConverter.GetBytes((short)8).CopyTo(data, 28);
        BitConverter.GetBytes(rowBytes * height).CopyTo(data, 34);
        BitConverter.GetBytes(256).CopyTo(data, 46);
        for (int i = 0; i < 256; i++)
        {
            int p = 54 + i * 4;
            data[p] = data[p + 1] = data[p + 2] = (byte)i;
        }
        for (int y = 0; y < height; y++)
            pixels.Slice(y * width, width).CopyTo(data.AsSpan(offset + (height - 1 - y) * rowBytes));
        return data;
    }

    public static (byte[] Pixels, int Width, int Height) DecodeGray(byte[] data)
    {
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G')
            throw new InvalidDataException("a PNG image saved with a .bmp name: save it as BMP, or open it with an image library");
        if (data.Length < 54 || data[0] != 'B' || data[1] != 'M') throw new InvalidDataException("not a BMP file");
        int offset = BitConverter.ToInt32(data, 10);
        int headerSize = BitConverter.ToInt32(data, 14);
        int width = BitConverter.ToInt32(data, 18);
        int rawHeight = BitConverter.ToInt32(data, 22);
        int bpp = BitConverter.ToInt16(data, 28);
        int compression = BitConverter.ToInt32(data, 30);
        // 0 = uncompressed; 3 = bit fields, which 32-bit BMPs use for plain BGRA
        if (compression != 0 && !(compression == 3 && bpp == 32)) throw new InvalidDataException("compressed BMP not supported");
        if (width <= 0 || rawHeight == 0) throw new InvalidDataException("bad BMP size");
        bool bottomUp = rawHeight > 0;
        int height = Math.Abs(rawHeight);
        int rowBytes = (width * bpp / 8 + 3) & ~3;
        if ((long)offset + (long)rowBytes * height > data.Length) throw new InvalidDataException("truncated BMP");

        var gray = new byte[width * height];
        byte[]? palette = null;
        if (bpp == 8)
        {
            palette = new byte[256];
            for (int i = 0; i < 256; i++) palette[i] = (byte)i;
            int pal = 14 + headerSize;
            for (int i = 0; i < 256 && pal + i * 4 + 2 < offset; i++)
                palette[i] = (byte)((data[pal + i * 4] + data[pal + i * 4 + 1] + data[pal + i * 4 + 2]) / 3);
        }
        else if (bpp != 24 && bpp != 32)
        {
            throw new InvalidDataException($"{bpp}-bit BMP not supported");
        }

        for (int y = 0; y < height; y++)
        {
            int src = offset + (bottomUp ? height - 1 - y : y) * rowBytes;
            int dst = y * width;
            if (bpp == 8)
                for (int x = 0; x < width; x++) gray[dst + x] = palette![data[src + x]];
            else
            {
                int step = bpp / 8; // 3 (BGR) or 4 (BGRA)
                for (int x = 0; x < width; x++)
                    gray[dst + x] = (byte)((data[src + step * x] + data[src + step * x + 1] + data[src + step * x + 2]) / 3);
            }
        }
        return (gray, width, height);
    }
}
