using System;
using System.IO;
using BarcodeReading;

// Demo: decode one image the way the line software will.
// usage: ReaderDemo <image.bmp> <qr|dm|code128|ean13> <codes-per-case>

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: ReaderDemo <image.bmp> <qr|dm|code128|ean13> <codes-per-case>");
    return 2;
}

var format = args[1].ToLowerInvariant() switch
{
    "dm" or "datamatrix" => CodeFormat.DataMatrix,
    "code128" or "gs1-128" => CodeFormat.Gs1_128,
    "ean13" => CodeFormat.Ean13,
    _ => CodeFormat.QrCode,
};

var (pixels, width, height) = BmpImage.LoadGray(args[0]);
using var reader = new CodeReader(new ReaderSettings { Format = format, ExpectedCount = int.Parse(args[2]) });

reader.Decode(pixels, width, height, width); // warm-up
var result = reader.Decode(pixels, width, height, width);

Console.WriteLine($"engine {CodeReader.EngineVersion}: {result.Stats.Codes}/{result.Stats.Expected} codes in {result.Stats.TotalMs:F0} ms" +
                  $" (locate {result.Stats.LocateMs:F0} ms), label brightness {result.Stats.LabelBrightness:F0}/255" +
                  (result.Stats.Complete ? "" : $", {result.Misses.Count} unread regions"));
foreach (var c in result.Codes)
{
    var gs1 = c.Gs1 is { } g ? $"  GTIN {g.Gtin} batch {g.Batch} exp {g.Expiry} serial {g.Serial}{(g.CheckDigitValid ? "" : " BAD CHECK DIGIT")}" : "";
    Console.WriteLine($"  [{c.Step,-8}] ({c.Corners[0].X,5:F0},{c.Corners[0].Y,5:F0}) {Printable(c.Text)}{gs1}");
}
return result.Stats.Complete ? 0 : 1;

static string Printable(string s) => s.Replace("\u001D", "<GS>");
