using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using BarcodeReading.Protection;

// Build-time tool: encrypt the AI models and other artifacts at rest with the package content key, so the
// shipped package holds only encrypted files (no plaintext models). The plaintext stays in the repo / on the
// build machine and is never shipped. The same content key is wrapped per installation by the licence server
// (docs/vision-system/LICENSING.md section 7.4); the line PC decrypts in memory with it.
//
// Usage:
//   vs-protect keygen                                  print a fresh 32-byte content key (base64); keep it secret
//   vs-protect encrypt --key <b64|hex> <files...>      encrypt each file in place (content becomes a .vsenc container)
//   vs-protect encrypt --key <b64|hex> --out <dir> <files...>   write encrypted copies into <dir>, same names
//   vs-protect decrypt --key <b64|hex> --out <dir> <files...>   decrypt (for checking); writes plaintext to <dir>
//   vs-protect verify  --key <b64|hex> <files...>      decrypt in memory and report OK/FAIL, write nothing
//
// The key may also come from the VS_CONTENT_KEY environment variable or --key-file <path>. The logical name
// (GCM associated data) is the file's name, matching the runtime loader. Never commit the real content key.

namespace VisionTools.ProtectArtifacts;

internal static class Program
{
    private static int Main(string[] args)
    {
        try { return Run(args); }
        catch (ToolError e) { Console.Error.WriteLine("vs-protect: " + e.Message); return 2; }
        catch (ContentProtectionException e) { Console.Error.WriteLine("vs-protect: " + e.Message); return 1; }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0) { Usage(); return 2; }
        var cmd = args[0];
        if (cmd is "-h" or "--help" or "help") { Usage(); return 0; }

        if (cmd == "keygen")
        {
            Console.WriteLine(Convert.ToBase64String(RandomNumberGenerator.GetBytes(ContentCipher.KeySize)));
            return 0;
        }

        if (cmd is not ("encrypt" or "decrypt" or "verify"))
            throw new ToolError($"unknown command '{cmd}'. Try: keygen, encrypt, decrypt, verify.");

        string? keyText = Environment.GetEnvironmentVariable("VS_CONTENT_KEY");
        string? outDir = null;
        var files = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--key": keyText = Next(args, ref i); break;
                case "--key-file": keyText = File.ReadAllText(Next(args, ref i)).Trim(); break;
                case "--out": outDir = Next(args, ref i); break;
                default:
                    if (args[i].StartsWith("--")) throw new ToolError($"unknown option '{args[i]}'.");
                    files.Add(args[i]);
                    break;
            }
        }
        if (files.Count == 0) throw new ToolError("no files given.");
        if (string.IsNullOrWhiteSpace(keyText)) throw new ToolError("no content key: pass --key, --key-file or set VS_CONTENT_KEY.");
        byte[] key = ParseKey(keyText);
        if (key.Length != ContentCipher.KeySize) throw new ToolError($"content key must be {ContentCipher.KeySize} bytes ({ContentCipher.KeySize * 8}-bit); got {key.Length}.");
        if (outDir is not null) Directory.CreateDirectory(outDir);

        int fail = 0;
        try
        {
            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                var raw = File.ReadAllBytes(f);
                switch (cmd)
                {
                    case "encrypt":
                    {
                        if (ContentCipher.LooksEncrypted(raw)) { Console.WriteLine($"skip (already encrypted): {f}"); break; }
                        var enc = ContentCipher.Encrypt(raw, key, name);
                        var dest = outDir is null ? f : Path.Combine(outDir, name);
                        File.WriteAllBytes(dest, enc);
                        Console.WriteLine($"encrypted {f} -> {dest} ({raw.Length} -> {enc.Length} bytes)");
                        break;
                    }
                    case "decrypt":
                    {
                        if (outDir is null) throw new ToolError("decrypt needs --out <dir> so it never overwrites an encrypted file with plaintext.");
                        var plain = ContentCipher.Decrypt(raw, key, name);
                        var dest = Path.Combine(outDir, name);
                        File.WriteAllBytes(dest, plain);
                        Console.WriteLine($"decrypted {f} -> {dest} ({raw.Length} -> {plain.Length} bytes)");
                        break;
                    }
                    case "verify":
                    {
                        try { var plain = ContentCipher.Decrypt(raw, key, name); Console.WriteLine($"OK   {f} ({plain.Length} bytes)"); }
                        catch (ContentProtectionException e) { Console.WriteLine($"FAIL {f}: {e.Message}"); fail++; }
                        break;
                    }
                }
            }
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        return fail == 0 ? 0 : 1;
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ToolError($"'{args[i]}' needs a value.");
        return args[++i];
    }

    private static byte[] ParseKey(string text)
    {
        text = text.Trim();
        // hex (64 chars) or base64
        if (text.Length == ContentCipher.KeySize * 2 && IsHex(text))
        {
            var b = new byte[ContentCipher.KeySize];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(text.Substring(i * 2, 2), 16);
            return b;
        }
        try { return Convert.FromBase64String(text); }
        catch (FormatException) { throw new ToolError("content key is not valid base64 or 64-char hex."); }
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s) if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    private static void Usage()
    {
        Console.WriteLine(
@"vs-protect - encrypt AI models and data at rest with the package content key (AES-256-GCM).

  vs-protect keygen
  vs-protect encrypt --key <b64|hex> [--out <dir>] <files...>
  vs-protect decrypt --key <b64|hex> --out <dir> <files...>
  vs-protect verify  --key <b64|hex> <files...>

Key source: --key, --key-file <path>, or the VS_CONTENT_KEY environment variable.
The plaintext stays on the build machine; ship only the encrypted files. Never commit the key.");
    }

    private sealed class ToolError : Exception
    {
        public ToolError(string message) : base(message) { }
    }
}
