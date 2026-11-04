// rs-package: makes and checks signed software packages (DESIGN.md section 3, capability 7). Runs on the vendor's signing
// PC only; the private key never leaves it. The key pair is the one Vision System's PackageSign makes (ECDSA P-256), so one
// key can sign both.
//
//   rs-package keygen <private.pem> <public.pem>
//   rs-package pack <folder> <private.pem> --product <name> --version <v> --out <file.rspkg> [--min-version <v>] [--expires <yyyy-mm-dd>]
//       --min-version: once installed, nothing below it may go on again (a release that fixes a flaw names itself).
//       --expires: the package is refused after this day, so it cannot be held back and installed later.
//   rs-package verify <file.rspkg> <public.pem> --product <name>
using System.Security.Cryptography;
using Sundaram.RemoteService;

string? Opt(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

switch (args)
{
    case ["keygen", var priv, var pub, ..]:
    {
        if (File.Exists(priv)) { Console.Error.WriteLine($"{priv} exists; not overwritten"); return 2; }
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(priv, ec.ExportECPrivateKeyPem());
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(priv, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(pub, ec.ExportSubjectPublicKeyInfoPem());
        Console.WriteLine($"wrote {priv} (keep secret, on the signing PC only) and {pub} (ship it with the program)");
        return 0;
    }
    case ["pack", var folder, var key, ..]:
    {
        var (product, version, output) = (Opt("--product"), Opt("--version"), Opt("--out"));
        if (product is null || version is null || output is null) { Console.Error.WriteLine("pack needs --product, --version and --out"); return 2; }
        if (!Directory.Exists(folder)) { Console.Error.WriteLine($"no such folder: {folder}"); return 2; }
        using var ec = ECDsa.Create();
        ec.ImportFromPem(File.ReadAllText(key));
        DateTime? expires = null;
        if (Opt("--expires") is { } e)
        {
            if (!DateTime.TryParseExact(e, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d))
            { Console.Error.WriteLine("--expires is yyyy-mm-dd"); return 2; }
            expires = d.AddDays(1);   // the whole named day
        }
        if (Opt("--min-version") is { } min && VersionOrder.Compare(version, min) is not >= 0)
        { Console.Error.WriteLine($"--min-version {min} is above the package's own version {version}"); return 2; }
        var m = PackageFile.Pack(folder, product, version, ec, output, Opt("--min-version"), expires);
        Console.WriteLine($"{output}: {product} {version}, {m.Files.Count} files, SHA-256 {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))).ToLowerInvariant()}");
        return 0;
    }
    case ["verify", var package, var pub, ..]:
    {
        var into = Path.Combine(Path.GetTempPath(), "rs-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(into);
        try
        {
            var m = PackageFile.Open(package, into, [File.ReadAllText(pub)], Opt("--product") ?? "", 1L << 34);
            Console.WriteLine($"good: {m.Product} {m.Version}, {m.Files.Count} files, signed {m.CreatedUtc:u}");
            return 0;
        }
        catch (PackageRefused e) { Console.Error.WriteLine("refused: " + e.Message); return 3; }
        finally { Directory.Delete(into, true); }
    }
    default:
        Console.Error.WriteLine("""
            rs-package keygen <private.pem> <public.pem>
            rs-package pack <folder> <private.pem> --product <name> --version <v> --out <file.rspkg> [--min-version <v>] [--expires <yyyy-mm-dd>]
            rs-package verify <file.rspkg> <public.pem> --product <name>
            """);
        return 2;
}
