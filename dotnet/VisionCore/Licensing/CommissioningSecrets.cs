using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VisionCore.Licensing;

/// <summary>Wraps and unwraps the commissioning secrets file (Req 17.10 MRG-09). DPAPI on Windows; a test double in tests.</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plain);

    /// <summary>Throws <see cref="CryptographicException"/> when the blob was protected elsewhere or is damaged.</summary>
    byte[] Unprotect(byte[] blob);
}

/// <summary>
/// Windows DPAPI, machine scope, with a fixed extra entropy (Req 17.10 MRG-09 item 1): Vision Studio and the line
/// runtime run as different service accounts and both read the secrets; the data folder's NTFS rights keep other
/// users out (REQ-16 item 2), and a copy taken to another PC cannot be unwrapped.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private const int UiForbidden = 0x1, LocalMachine = 0x4;
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("vs-station-v1");

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    public byte[] Protect(byte[] plain) => Run(plain, protect: true);
    public byte[] Unprotect(byte[] blob) => Run(blob, protect: false);

    private static byte[] Run(byte[] input, bool protect)
    {
        var inH = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entH = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var inBlob = new DataBlob { Size = input.Length, Data = inH.AddrOfPinnedObject() };
            var entBlob = new DataBlob { Size = Entropy.Length, Data = entH.AddrOfPinnedObject() };
            DataBlob outBlob;
            var ok = protect
                ? CryptProtectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden | LocalMachine, out outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outBlob);
            if (!ok) throw new CryptographicException(Marshal.GetLastPInvokeError());
            try
            {
                var result = new byte[outBlob.Size];
                Marshal.Copy(outBlob.Data, result, 0, outBlob.Size);
                return result;
            }
            finally
            {
                if (!protect && outBlob.Size > 0) Marshal.Copy(new byte[outBlob.Size], 0, outBlob.Data, outBlob.Size); // wipe the plain copy
                LocalFree(outBlob.Data);
            }
        }
        finally { inH.Free(); entH.Free(); }
    }
}

/// <summary>
/// The service and factory OTP secrets and the results ledger key of this line PC (Req 17.10 MRG-01, MRG-04, MRG-09).
/// Set at commissioning into one protected file in the data folder (<see cref="FileName"/>; the whole file is one
/// blob, so neither names nor values can be read from it). A value comes from the store first, then from the service
/// account's environment (bench PCs, older installations). A store that exists but cannot be opened gives no value
/// at all, not the environment's (fail closed); <see cref="Error"/> says why. Values are never logged or shown.
/// </summary>
public sealed class CommissioningSecrets
{
    public const string ServiceOtpSecret = "VS_SERVICE_OTP_SECRET";
    public const string FactoryOtpSecret = "VS_FACTORY_OTP_SECRET";
    public const string LedgerKey = "VS_LEDGER_KEY";
    /// <summary>The line PC's token for the remote support relay (support.sundaramtech.com, REQ-11), printed once by the relay.</summary>
    public const string SupportLinkToken = "VS_SUPPORT_LINK_TOKEN";
    public const int MinSupportLinkTokenChars = 32;
    public const int MinLedgerKeyBytes = 32;   // DatabaseSecurity.MinKeyBytes
    public const string FileName = "station.dat";

    public static readonly string[] Names = [ServiceOtpSecret, FactoryOtpSecret, LedgerKey, SupportLinkToken];

    private readonly string _path;
    private readonly ISecretProtector? _protector;
    private readonly Func<string, string?> _environment;
    private readonly object _lock = new();

    /// <param name="protector">The platform protection; null where there is none (the store then neither reads nor writes).</param>
    /// <param name="environment">Reads an environment variable; the process environment when null.</param>
    public CommissioningSecrets(string dataFolder, ISecretProtector? protector, Func<string, string?>? environment = null)
    {
        _path = Path.Combine(dataFolder, FileName);
        _protector = protector;
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>The store of this PC: DPAPI on Windows, none elsewhere.</summary>
    public static CommissioningSecrets ForThisPc(string dataFolder)
        => new(dataFolder, OperatingSystem.IsWindows() ? new DpapiSecretProtector() : null);

    public string FilePath => _path;
    public bool CanStore => _protector is not null;

    /// <summary>Why the store cannot be used (never a value), or null.</summary>
    public string? Error
    {
        get
        {
            if (!File.Exists(_path)) return null;
            if (_protector is null) return $"{FileName} exists but this PC has no protection to open it; no secret is used.";
            try { Load(); return null; }
            catch (Exception e) when (e is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
            {
                return $"{FileName} cannot be opened (made on another PC, or damaged); no secret is used.";
            }
        }
    }

    public string? ServiceOtp => Get(ServiceOtpSecret);
    public string? FactoryOtp => Get(FactoryOtpSecret);
    /// <summary>The ledger key as base64; the hosts parse it with DatabaseSecurity.ParseKey.</summary>
    public string? LedgerKeyBase64 => Get(LedgerKey);
    public string? SupportLink => Get(SupportLinkToken);

    /// <summary>The value of <paramref name="name"/>, or null (also when the store cannot be opened).</summary>
    public string? Get(string name) => Lookup(name).Value;

    /// <summary>Where <paramref name="name"/> comes from: "store", "environment", or null when it is not set.</summary>
    public string? SourceOf(string name) => Lookup(name).Source;

    private (string? Value, string? Source) Lookup(string name)
    {
        CheckName(name);
        if (File.Exists(_path))
        {
            Dictionary<string, string> stored;
            try
            {
                if (_protector is null) return (null, null);
                stored = Load();
            }
            catch (Exception e) when (e is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
            {
                return (null, null);   // fail closed: never fall back to the environment over a store we cannot open
            }
            if (stored.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)) return (v, "store");
        }
        var env = _environment(name);
        return string.IsNullOrWhiteSpace(env) ? (null, null) : (env, "environment");
    }

    /// <summary>
    /// Stores <paramref name="value"/> under <paramref name="name"/> after checking it (OTP secrets at least
    /// <see cref="OemOtpVerifier.MinSecretBytes"/> bytes, the ledger key base64 of at least 32 bytes). An existing
    /// ledger key is replaced only with <paramref name="replace"/>: a new key leaves the rows chained before it
    /// unverifiable.
    /// </summary>
    public void Set(string name, string value, bool replace = false)
    {
        CheckName(name);
        if (_protector is null) throw new InvalidOperationException("This PC has no secret protection (DPAPI is Windows only); nothing stored.");
        value = (value ?? "").Trim();
        var problem = Check(name, value);
        if (problem is not null) throw new ArgumentException(problem);
        lock (_lock)
        {
            var stored = File.Exists(_path) ? Load() : new Dictionary<string, string>();
            if (name == LedgerKey && !replace && stored.TryGetValue(LedgerKey, out var old) && old != value)
                throw new InvalidOperationException("A ledger key is already stored; a new one leaves the rows chained before it unverifiable. Use --replace to replace it.");
            stored[name] = value;
            Save(stored);
        }
    }

    /// <summary>Makes and stores a random 32-byte ledger key (Req 17.10 MRG-04).</summary>
    public void NewLedgerKey(bool replace = false) => Set(LedgerKey, Convert.ToBase64String(RandomNumberGenerator.GetBytes(MinLedgerKeyBytes)), replace);

    /// <summary>Why <paramref name="value"/> is not usable as <paramref name="name"/>, or null.</summary>
    public static string? Check(string name, string value)
    {
        if (name == LedgerKey)
        {
            try { return Convert.FromBase64String(value).Length >= MinLedgerKeyBytes ? null : $"The ledger key must be at least {MinLedgerKeyBytes} bytes."; }
            catch (FormatException) { return "The ledger key must be base64."; }
        }
        if (name == SupportLinkToken)
            return value.Length >= MinSupportLinkTokenChars ? null : $"The support link token must be at least {MinSupportLinkTokenChars} characters.";
        return Encoding.UTF8.GetByteCount(value) >= OemOtpVerifier.MinSecretBytes
            ? null : $"An OTP secret must be at least {OemOtpVerifier.MinSecretBytes} bytes.";
    }

    private static void CheckName(string name)
    {
        if (!Names.Contains(name)) throw new ArgumentException("Unknown secret name: " + name);
    }

    private Dictionary<string, string> Load()
    {
        var plain = _protector!.Unprotect(File.ReadAllBytes(_path));
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? new(); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private void Save(Dictionary<string, string> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var plain = JsonSerializer.SerializeToUtf8Bytes(values);
        byte[] blob;
        try { blob = _protector!.Protect(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, blob);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, _path, overwrite: true);
    }
}
