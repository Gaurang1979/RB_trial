using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VisionReports;

/// <summary>Raised when manual or out-of-band tampering with results.db is detected (REQ-16 / PROT-31).</summary>
public sealed class DatabaseTamperingException : Exception
{
    public DatabaseTamperingException(string message) : base(message) { }
}

/// <summary>The newest chained row of one table as the chain head keeps it: its id and hash (id 0 = none yet).</summary>
public sealed record HeadEntry(long Id, string Hash)
{
    public static readonly HeadEntry None = new(0, DatabaseSecurity.GenesisHash);
}

/// <summary>The chain head kept outside results.db (Req 17.10 MRG-08 item 5): the newest chained row of each table.</summary>
public sealed record ChainHead(HeadEntry Inspections, HeadEntry Parts)
{
    public static readonly ChainHead Empty = new(HeadEntry.None, HeadEntry.None);
    public HeadEntry For(string table) => table == "inspections" ? Inspections : Parts;
    public ChainHead With(string table, HeadEntry e) => table == "inspections" ? this with { Inspections = e } : this with { Parts = e };
}

/// <summary>
/// HMAC-SHA256 chained ledger for the results log (Req 17.10 REQ-16 / PROT-31): an out-of-band edit of a chained row
/// breaks the chain. The key is a random per-installation key set at commissioning (VS_LEDGER_KEY, base64, at least
/// 32 bytes), never in the repository and not tied to the PC's name, so renaming the PC or moving results.db does
/// not make good rows look tampered.
/// </summary>
public static class DatabaseSecurity
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";
    public const int MinKeyBytes = 32;

    /// <summary>The chain version stored in a row's chain_ver column: empty is v1 (the first formula, time, batch,
    /// job, camera or part number and verdict only), "2" is v2, every recorded field (Req 17.10 MRG-08).</summary>
    public const string ChainV2 = "2";

    /// <summary>The ledger key from the service account's environment, or null when it is not set or too short.</summary>
    public static byte[]? KeyFromEnvironment() => ParseKey(Environment.GetEnvironmentVariable("VS_LEDGER_KEY"));

    public static byte[]? ParseKey(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        try
        {
            var key = Convert.FromBase64String(base64.Trim());
            return key.Length >= MinKeyBytes ? key : null;
        }
        catch (FormatException) { return null; }
    }

    /// <summary>v2 row hash (Req 17.10 MRG-08): HMAC-SHA256 over "vs-ledger-v2|table|prev" and then every recorded
    /// field in column order, each as "|len:value" (a null as "|-"), so no value can shift into its neighbour.
    /// Values are taken as SQLite stores and returns them (text, integer, real, null), so the writer's parameters and
    /// the verifier's reader give the same text.</summary>
    public static string ComputeRowHashV2(string prevHash, string table, IReadOnlyList<object?> values, byte[] key)
    {
        var prev = string.IsNullOrWhiteSpace(prevHash) ? GenesisHash : prevHash.Trim();
        var sb = new StringBuilder("vs-ledger-v2|").Append(table).Append('|').Append(prev);
        foreach (var v in values)
        {
            var s = Canonical(v);
            if (s is null) sb.Append("|-");
            else sb.Append('|').Append(s.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(s);
        }
        using var hmac = new HMACSHA256(key);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static string? Canonical(object? v) => v switch
    {
        null or DBNull => null,
        string s => s,
        bool b => b ? "1" : "0",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => ((double)f).ToString("R", CultureInfo.InvariantCulture),
        IFormattable n => n.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new ArgumentException($"Ledger field of type {v.GetType().Name} has no canonical form"),
    };

    /// <summary>Computes chained cryptographic HMAC for an inspection entry (chain v1, kept to verify old rows).</summary>
    public static string ComputeInspectionHash(string prevHash, string timeUtc, string batch, string job, int camera, int? partId, bool ok, byte[] key)
    {
        var prev = string.IsNullOrWhiteSpace(prevHash) ? GenesisHash : prevHash.Trim();
        var data = $"{prev}|{timeUtc}|{batch}|{job}|{camera}|{partId}|{ok}";
        using var hmac = new HMACSHA256(key);
        var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>Computes chained cryptographic HMAC for a part verdict entry (chain v1, kept to verify old rows).</summary>
    public static string ComputePartHash(string prevHash, string timeUtc, string batch, string job, int partId, bool rejected, byte[] key)
    {
        var prev = string.IsNullOrWhiteSpace(prevHash) ? GenesisHash : prevHash.Trim();
        var data = $"{prev}|{timeUtc}|{batch}|{job}|{partId}|{rejected}";
        using var hmac = new HMACSHA256(key);
        var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private const string HeadMagic = "vs-ledger-head-v1";

    /// <summary>The chain head file's text (Req 17.10 MRG-08 item 5): a line per table ("table id hash") and an
    /// HMAC-SHA256 over them with the ledger key, so the head cannot be written without the key.</summary>
    public static string FormatHead(ChainHead head, byte[] key)
    {
        var body = HeadBody(head);
        return body + "\n" + HeadMac(body, key) + "\n";
    }

    /// <summary>Reads a chain head file's text. Throws <see cref="DatabaseTamperingException"/> when it is not a
    /// head this key wrote (format or HMAC wrong).</summary>
    public static ChainHead ParseHead(string text, byte[] key)
    {
        var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 4 || lines[0] != HeadMagic)
            throw new DatabaseTamperingException("Database tampering detected: the results ledger's chain head file is not readable.");
        var head = new ChainHead(Entry(lines[1], "inspections"), Entry(lines[2], "parts"));
        var expected = HeadMac(HeadBody(head), key);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(lines[3].Trim().ToLowerInvariant())))
            throw new DatabaseTamperingException("Database tampering detected: the results ledger's chain head file was changed.");
        return head;
    }

    private static HeadEntry Entry(string line, string table)
    {
        var p = line.Split(' ');
        if (p.Length != 3 || p[0] != table || !long.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || p[2].Length != 64)
            throw new DatabaseTamperingException("Database tampering detected: the results ledger's chain head file is not readable.");
        return new HeadEntry(id, p[2].ToLowerInvariant());
    }

    private static string HeadBody(ChainHead h) =>
        string.Create(CultureInfo.InvariantCulture, $"{HeadMagic}\ninspections {h.Inspections.Id} {h.Inspections.Hash}\nparts {h.Parts.Id} {h.Parts.Hash}");

    private static string HeadMac(string body, byte[] key)
    {
        using var hmac = new HMACSHA256(key);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }
}
