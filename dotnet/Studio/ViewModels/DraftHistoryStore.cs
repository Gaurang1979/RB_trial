using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.ViewModels;

/// <summary>Wraps and unwraps a kept draft: DPAPI on Windows, a test double in tests.</summary>
public interface IDraftProtector
{
    byte[] Protect(byte[] plain);

    /// <summary>Throws <see cref="CryptographicException"/> when the blob was protected elsewhere or is damaged.</summary>
    byte[] Unprotect(byte[] blob);
}

/// <summary>
/// Windows DPAPI, this user only, with a fixed extra entropy: the undo history is the engineer's own, and a copy
/// taken to another user or PC cannot be opened. A draft holds the job's taught settings (AGENTS.md IP rule 3).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiDraftProtector : IDraftProtector
{
    private const int UiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("vs-studio-draft-v1");

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
                ? CryptProtectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outBlob)
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

/// <summary>A job's draft kept across a restart: its undo steps, the draft now, its redo steps and the dropped tools
/// that found nothing (camera, name, area), with what it is matched by when the job is opened again: the hash of the
/// job file as opened (<paramref name="Saved"/>), or, after a Save here, the version that Save gave
/// (<paramref name="SavedVersion"/>; the runtime writes the file its own way, and a save anywhere else gives a new
/// version). <paramref name="Accepted"/>: the auto find suggestions accepted as tools, found on the picture
/// <paramref name="AcceptedOn"/>, so an undo of Accept after the restart offers the suggestion again (null in a file
/// kept before they were).</summary>
public sealed record KeptDraft(
    string? Saved,
    int SavedVersion,
    IReadOnlyList<string> Undo,
    string Current,
    IReadOnlyList<string> Redo,
    IReadOnlyList<KeptArea> NotSnapped,
    IReadOnlyList<KeptSuggestion>? Accepted = null,
    string? AcceptedOn = null);

public sealed record KeptArea(int Camera, string Name, int X, int Y, int Width, int Height);

/// <summary>A suggestion accepted as the tool <paramref name="Tool"/> of a camera; <paramref name="Offered"/>: listed
/// again (its Accept undone) when the history was kept.</summary>
public sealed record KeptSuggestion(int Camera, string Tool, ToolSuggestion Suggestion, bool Offered);

/// <summary>
/// The designer's undo history kept across a restart of Vision Studio for the draft (UI-UX-SPEC.md 5, AS-18): one
/// file per job in the user's local application data, named by a hash of the job name (no job name on disk), the
/// whole file compressed and protected (<see cref="IDraftProtector"/>). With no protection (macOS, Linux, the
/// tests' default) nothing is written or read. A file that cannot be read is dropped: the job opens as saved.
/// </summary>
public sealed class DraftHistoryStore(string? folder, IDraftProtector? protector)
{
    /// <summary>Keeps nothing (declared first: <see cref="Shared"/> uses it).</summary>
    public static DraftHistoryStore None { get; } = new(null, null);

    /// <summary>The app's own: DPAPI in the user's local application data on Windows, none elsewhere.</summary>
    public static DraftHistoryStore Shared { get; set; } = OperatingSystem.IsWindows()
        ? new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisionStudio", "drafts"),
            new DpapiDraftProtector())
        : None;

    public bool Keeps => folder is not null && protector is not null;

    /// <summary>The hash a kept draft is matched with: the saved job file as Vision Studio opened or saved it.</summary>
    public static string Hash(string savedJson) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(savedJson)));

    /// <summary>The file of a job (for the tests); null when nothing is kept.</summary>
    public string? FileOf(string job) => folder is null ? null
        : Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(job)))[..16].ToLowerInvariant() + ".dat");

    public void Save(string job, KeptDraft draft)
    {
        if (!Keeps || FileOf(job) is not { } file) return;
        try
        {
            using var plain = new MemoryStream();
            using (var zip = new BrotliStream(plain, CompressionLevel.Fastest, leaveOpen: true))
                JsonSerializer.Serialize(zip, draft);
            var blob = protector!.Protect(plain.ToArray());
            Directory.CreateDirectory(folder!);
            var temp = file + ".tmp";
            File.WriteAllBytes(temp, blob);
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException) { }   // not kept; undo still works
    }

    /// <summary>The draft kept for a job; null when none, or when it cannot be read (it is dropped then).</summary>
    public KeptDraft? Load(string job)
    {
        if (!Keeps || FileOf(job) is not { } file || !File.Exists(file)) return null;
        try
        {
            using var zip = new BrotliStream(new MemoryStream(protector!.Unprotect(File.ReadAllBytes(file))), CompressionMode.Decompress);
            if (JsonSerializer.Deserialize<KeptDraft>(zip) is { Current: not null, Undo: not null, Redo: not null } d && (d.Saved is not null || d.SavedVersion > 0))
                return d with { NotSnapped = d.NotSnapped ?? [], Accepted = d.Accepted ?? [] };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException
                                      or JsonException or InvalidDataException) { }
        Drop(job);
        return null;
    }

    public void Drop(string job)
    {
        if (FileOf(job) is not { } file) return;
        try { File.Delete(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
