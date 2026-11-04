using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text.Json;
using VisionCore.Integrity;

// PackageSign: build-time tool for the package integrity manifest (docs/vision-system/PACKAGE-PROTECTION.md).
// It is NOT shipped on the line; the private key stays on the vendor's signing machine.
//
//   PackageSign keygen <private.pem> <public.pem>
//       Makes an ECDSA P-256 key pair. Keep the private key secret; the public key (package-public.pem)
//       is copied into the package next to the program.
//
//   PackageSign sign <root> <private.pem> [--version <v>] [--exclude <glob,glob>] [--require <rel,rel>]
//       Hashes every file under <root> (except the manifest, the signature placeholder and the public key),
//       signs the list and writes <root>/package.manifest.json. --require lists files that must be present.
//
//   PackageSign verify <root> <public.pem>
//       Checks the signed manifest against the files on disk. Exit 0 when every file matches, 3 otherwise.

return args switch
{
    ["keygen", var priv, var pub] => KeyGen(priv, pub),
    ["sign", var root, var key, .. var rest] => Sign(root, key, rest),
    ["verify", var root, var key] => Verify(root, key),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("""
        PackageSign keygen <private.pem> <public.pem>
        PackageSign sign   <root> <private.pem> [--version <v>] [--exclude <glob,glob>] [--require <rel,rel>]
        PackageSign verify <root> <public.pem>
        """);
    return 2;
}

static int KeyGen(string privPath, string pubPath)
{
    using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(privPath, ec.ExportECPrivateKeyPem());
    File.WriteAllText(pubPath, ec.ExportSubjectPublicKeyInfoPem());
    Console.WriteLine($"wrote {privPath} (keep secret) and {pubPath} (ship as {PackageIntegrity.PublicKeyFile})");
    return 0;
}

static int Sign(string root, string keyPath, string[] rest)
{
    string? Opt(string name) => Array.IndexOf(rest, name) is var i and >= 0 && i + 1 < rest.Length ? rest[i + 1] : null;
    var version = Opt("--version") ?? "0.0.0";
    var excludes = (Opt("--exclude") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var required = (Opt("--require") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    if (!Directory.Exists(root)) { Console.Error.WriteLine($"no such folder: {root}"); return 2; }
    // never hash the manifest or the signature of this run, nor the public key (its bytes are a trust anchor,
    // not package content); everything else under root is covered.
    var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { PackageIntegrity.ManifestFile, PackageIntegrity.PublicKeyFile };
    var files = new List<string>();
    foreach (var abs in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(root, abs).Replace('\\', '/');
        if (skip.Contains(rel)) continue;
        if (excludes.Any(g => FileSystemName.MatchesSimpleExpression(g, rel) || FileSystemName.MatchesSimpleExpression(g, Path.GetFileName(rel)))) continue;
        files.Add(rel);
    }
    files.Sort(StringComparer.Ordinal);

    foreach (var req in required)
        if (!files.Contains(req.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase))
        { Console.Error.WriteLine($"required file not found in {root}: {req}"); return 3; }

    var manifest = PackageIntegrity.BuildManifest(root, files, version);
    using var ec = ECDsa.Create();
    ec.ImportFromPem(File.ReadAllText(keyPath));
    var signed = SignedManifest.Sign(manifest, ec);
    var outPath = Path.Combine(root, PackageIntegrity.ManifestFile);
    File.WriteAllText(outPath, JsonSerializer.Serialize(signed));
    Console.WriteLine($"signed {files.Count} files into {outPath} (package {version})");
    return 0;
}

static int Verify(string root, string keyPath)
{
    var manPath = Path.Combine(root, PackageIntegrity.ManifestFile);
    var key = File.Exists(keyPath) ? File.ReadAllText(keyPath) : null;
    var man = File.Exists(manPath) ? File.ReadAllText(manPath) : null;
    var r = PackageIntegrity.Check(root, key, man);
    Console.WriteLine($"{r.State}: {r.Detail}");
    return r.Ok ? 0 : 3;
}
