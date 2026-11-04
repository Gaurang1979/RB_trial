using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VisionCore.Integrity;

namespace VisionCore;

public sealed class RecipeManifest
{
    public string RecipeId { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled Recipe";
    public string Sku { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Author { get; set; } = "VisionStudio";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public int CameraCount { get; set; } = 1;
    public int TargetCycleTimeMs { get; set; } = 50;
    public string ChecksumSha256 { get; set; } = "";
}

/// <summary>What a bundle holds, after its signature and every entry's hash and size were checked. The taught
/// pictures are BMP bytes by the tool's place in the job; a bundle made before they were carried has none.</summary>
public sealed record RecipeContents(
    RecipeManifest Manifest,
    string ToolsJson,
    byte[] GoldenImage,
    byte[]? MaskImage,
    byte[]? Weights,
    string? PlcMappingJson,
    IReadOnlyDictionary<int, byte[]> Patterns,
    IReadOnlyDictionary<int, byte[]> References);

/// <summary>
/// Portable self-contained .vsrecipe packager and unpackager (inspired by Overview.ai .recipe format).
/// Encapsulates manifest, tool configurations, golden template image, weights, and PLC mappings.
/// Every bundle is signed with the plant key (ECDSA P-256, the <see cref="SignedManifest"/> of package
/// integrity over each entry's SHA-256), and an import refuses an unsigned, changed or over-size bundle
/// before anything is written (Req 17.11 AUD-06). Taught Find the part patterns and taught reference pictures go in
/// as <c>pattern-&lt;tool&gt;.bmp</c> and <c>reference-&lt;tool&gt;.bmp</c>, signed like every other entry (AUD-12).
/// </summary>
public static class RecipeBundle
{
    public const string ManifestEntry = "manifest.json";
    public const string ToolsEntry = "tools.json";
    public const string GoldenImageEntry = "golden.png";
    public const string MaskImageEntry = "mask.png";
    public const string WeightsEntry = "weights.bin";
    public const string PlcMappingEntry = "plc_mapping.json";
    public const string SignatureEntry = "signature.json";
    public const string PatternPrefix = "pattern-";
    public const string ReferencePrefix = "reference-";

    /// <summary>The fixed entries plus up to 56 taught pictures; the total stays at <see cref="MaxTotalBytes"/>.</summary>
    public const int MaxEntries = 64;
    public const long MaxTotalBytes = 256L * 1024 * 1024;
    /// <summary>The most bytes one taught pattern or reference picture may hold.</summary>
    public const long MaxTaughtImageBytes = 16L * 1024 * 1024;

    // one name per tool: no leading zeros, so "pattern-01.bmp" and "pattern-1.bmp" can never both be read
    private static readonly Regex TaughtName = new(@"^(pattern|reference)-(0|[1-9][0-9]{0,2})\.bmp\z", RegexOptions.CultureInvariant);

    public static string PatternEntry(int tool) => $"{PatternPrefix}{tool}.bmp";
    public static string ReferenceEntry(int tool) => $"{ReferencePrefix}{tool}.bmp";

    /// <summary>The most bytes each entry may hold; an entry not named here is refused.</summary>
    public static readonly IReadOnlyDictionary<string, long> MaxEntryBytes = new Dictionary<string, long>
    {
        [ManifestEntry] = 1024 * 1024,
        [ToolsEntry] = JobFile.MaxBytes,
        [GoldenImageEntry] = 32L * 1024 * 1024,
        [MaskImageEntry] = 32L * 1024 * 1024,
        [WeightsEntry] = 200L * 1024 * 1024,
        [PlcMappingEntry] = 1024 * 1024,
        [SignatureEntry] = 64 * 1024,
    };

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static void ExportToFile(
        string filePath,
        ECDsa signingKey,
        RecipeManifest manifest,
        string toolsJson,
        byte[] goldenImageBytes,
        byte[]? maskImageBytes = null,
        byte[]? weightsBytes = null,
        string? plcMappingJson = null,
        IReadOnlyDictionary<int, byte[]>? patterns = null,
        IReadOnlyDictionary<int, byte[]>? references = null)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(toolsJson);
        ArgumentNullException.ThrowIfNull(goldenImageBytes);

        byte[] toolsBytes = Utf8.GetBytes(toolsJson);
        manifest.ChecksumSha256 = Convert.ToHexString(SHA256.HashData(
            [.. toolsBytes, .. goldenImageBytes, .. weightsBytes ?? []]));

        // the exact bytes written are the bytes hashed and signed
        var entries = new List<(string Name, byte[] Bytes, CompressionLevel Level)>
        {
            (ManifestEntry, JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true }), CompressionLevel.Optimal),
            (ToolsEntry, toolsBytes, CompressionLevel.Optimal),
            (GoldenImageEntry, goldenImageBytes, CompressionLevel.NoCompression),
        };
        if (maskImageBytes is { Length: > 0 }) entries.Add((MaskImageEntry, maskImageBytes, CompressionLevel.Optimal));
        if (weightsBytes is { Length: > 0 }) entries.Add((WeightsEntry, weightsBytes, CompressionLevel.Fastest));
        if (!string.IsNullOrEmpty(plcMappingJson)) entries.Add((PlcMappingEntry, Utf8.GetBytes(plcMappingJson), CompressionLevel.Optimal));
        foreach (var (tool, bytes) in (patterns ?? new Dictionary<int, byte[]>()).OrderBy(p => p.Key))
            entries.Add((PatternEntry(tool), bytes, CompressionLevel.Optimal));
        foreach (var (tool, bytes) in (references ?? new Dictionary<int, byte[]>()).OrderBy(p => p.Key))
            entries.Add((ReferenceEntry(tool), bytes, CompressionLevel.Optimal));

        if (entries.Count + 1 > MaxEntries)
            throw new InvalidDataException($"the bundle would have {entries.Count + 1} entries; at most {MaxEntries}");
        long total = 0;
        foreach (var (name, bytes, _) in entries)
        {
            long cap = EntryCap(name) ?? throw new InvalidDataException($"{name} is not an entry a bundle can hold");
            if (bytes.Length > cap)
                throw new InvalidDataException($"{name} is {bytes.Length / 1048576.0:0.0} MB; a bundle allows at most {cap / 1048576.0:0.#} MB");
            total += bytes.Length;
        }
        if (total > MaxTotalBytes)
            throw new InvalidDataException($"the bundle would be {total / 1048576.0:0.0} MB; at most {MaxTotalBytes / 1048576} MB");

        var signed = new PackageManifest { Version = manifest.Version, CreatedUtc = DateTime.UtcNow };
        foreach (var (name, bytes, _) in entries) signed.Files[name] = Convert.ToHexString(SHA256.HashData(bytes));
        byte[] signature = JsonSerializer.SerializeToUtf8Bytes(SignedManifest.Sign(signed, signingKey));

        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = filePath + ".tmp";
        using (var fileStream = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
        {
            foreach (var (name, bytes, level) in entries.Append((SignatureEntry, signature, CompressionLevel.Optimal)))
            {
                using var s = archive.CreateEntry(name, level).Open();
                s.Write(bytes);
            }
        }
        File.Move(tmp, filePath, true);
    }

    /// <summary>
    /// Reads a bundle and checks it against the plant's public key. Refused with <see cref="InvalidDataException"/>
    /// (damaged, over-size, unknown or repeated entry) or <see cref="CryptographicException"/> (unsigned, signed by
    /// another key, an entry changed, missing or added). Nothing is written.
    /// </summary>
    public static RecipeContents ImportFromFile(string filePath, string publicKeyPem)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Recipe bundle file not found.", filePath);
        if (string.IsNullOrWhiteSpace(publicKeyPem))
            throw new CryptographicException("No plant public key is installed; a recipe bundle cannot be checked.");

        var read = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);
            if (archive.Entries.Count > MaxEntries)
                throw new InvalidDataException($"the bundle has {archive.Entries.Count} entries; at most {MaxEntries}");
            long total = 0;
            foreach (var e in archive.Entries)
            {
                // only the known flat names: no folders, no "..", no other files
                if (EntryCap(e.FullName) is not { } cap)
                    throw new InvalidDataException($"the bundle holds an unknown entry \"{e.FullName}\"");
                if (read.ContainsKey(e.FullName))
                    throw new InvalidDataException($"the bundle holds {e.FullName} twice");
                // the sizes in the zip directory can lie: count the bytes as they come out
                var bytes = ReadCapped(e, Math.Min(cap, MaxTotalBytes - total));
                total += bytes.Length;
                read[e.FullName] = bytes;
            }
        }
        catch (Exception e) when (e is not (InvalidDataException or CryptographicException))
        {
            throw new InvalidDataException($"the bundle cannot be read (damaged?): {e.Message}", e);
        }

        if (!read.TryGetValue(SignatureEntry, out var sigBytes))
            throw new CryptographicException("The bundle is not signed with the plant key.");
        SignedManifest? signed;
        try { signed = JsonSerializer.Deserialize<SignedManifest>(sigBytes); }
        catch (JsonException) { signed = null; }
        var list = signed?.Verify(publicKeyPem)
            ?? throw new CryptographicException("The bundle's signature is not the plant key's (another key, edited or damaged).");

        foreach (var name in read.Keys.Where(n => n != SignatureEntry))
            if (!list.Files.ContainsKey(name))
                throw new CryptographicException($"{name} was added to the bundle after it was signed.");
        foreach (var (name, want) in list.Files)
        {
            if (!read.TryGetValue(name, out var bytes) || name == SignatureEntry)
                throw new CryptographicException($"{name} is missing from the bundle.");
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), want, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException($"{name} was changed after the bundle was signed.");
        }

        RecipeManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<RecipeManifest>(Need(read, ManifestEntry))
                ?? throw new InvalidDataException("manifest.json is empty.");
        }
        catch (JsonException e) { throw new InvalidDataException($"manifest.json is not valid: {e.Message}", e); }

        var patterns = new Dictionary<int, byte[]>();
        var references = new Dictionary<int, byte[]>();
        foreach (var (name, bytes) in read)
            if (TaughtName.Match(name) is { Success: true } m)
                (m.Groups[1].Value == "pattern" ? patterns : references)[int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)] = bytes;

        return new RecipeContents(
            manifest,
            Text(Need(read, ToolsEntry), ToolsEntry),
            Need(read, GoldenImageEntry),
            read.GetValueOrDefault(MaskImageEntry),
            read.GetValueOrDefault(WeightsEntry),
            read.TryGetValue(PlcMappingEntry, out var plc) ? Text(plc, PlcMappingEntry) : null,
            patterns,
            references);
    }

    // the cap of a known entry; null = a name a bundle never holds (folders, "..", anything else)
    private static long? EntryCap(string name) =>
        MaxEntryBytes.TryGetValue(name, out long cap) ? cap : TaughtName.IsMatch(name) ? MaxTaughtImageBytes : null;

    private static byte[] Need(Dictionary<string, byte[]> read, string name) =>
        read.TryGetValue(name, out var b) ? b : throw new InvalidDataException($"Missing {name} in recipe bundle.");

    private static string Text(byte[] bytes, string name) =>
        System.Text.Unicode.Utf8.IsValid(bytes) ? Utf8.GetString(bytes) : throw new InvalidDataException($"{name} is not UTF-8 text.");

    private static byte[] ReadCapped(ZipArchiveEntry e, long cap)
    {
        if (e.Length > cap) throw TooBig(e.FullName, cap);
        using var s = e.Open();
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            if (ms.Length + n > cap) throw TooBig(e.FullName, cap);
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    private static InvalidDataException TooBig(string name, long cap) =>
        new($"{name} is larger than the {cap / 1048576.0:0.#} MB a bundle allows (or the bundle passes {MaxTotalBytes / 1048576} MB)");
}
