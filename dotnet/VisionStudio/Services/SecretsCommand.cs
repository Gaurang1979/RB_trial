using VisionCore.Licensing;

namespace VisionStudio.Services;

/// <summary>
/// "VisionStudio secrets ..." at commissioning (Req 17.10 MRG-09 item 3). A value is read from standard input, never
/// the command line (other users and the shell history see that), and is never printed back.
/// </summary>
public static class SecretsCommand
{
    public static int Run(string[] args, CommissioningSecrets store, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var replace = args.Contains("--replace");
        var rest = args.Where(a => a != "--replace").ToArray();
        try
        {
            switch (rest.FirstOrDefault())
            {
                case "status":
                    if (store.Error is { } e) stderr.WriteLine(e);
                    foreach (var n in CommissioningSecrets.Names)
                        stdout.WriteLine($"{n}: {(store.SourceOf(n) is { } src ? "set (" + src + ")" : "not set")}");
                    return store.Error is null ? 0 : 1;
                case "set" when rest.Length == 2:
                    store.Set(rest[1], stdin.ReadLine() ?? "", replace);
                    stdout.WriteLine($"{rest[1]} stored in {CommissioningSecrets.FileName}");
                    return 0;
                case "new-ledger-key" when rest.Length == 1:
                    store.NewLedgerKey(replace);
                    stdout.WriteLine($"{CommissioningSecrets.LedgerKey} made and stored in {CommissioningSecrets.FileName}");
                    return 0;
                default:
                    stderr.WriteLine("secrets status | secrets set <" + string.Join("|", CommissioningSecrets.Names) + "> [--replace] (value on standard input) | secrets new-ledger-key [--replace]");
                    return 2;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Security.Cryptography.CryptographicException
                                       or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            stderr.WriteLine(ex is System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException
                ? $"{CommissioningSecrets.FileName} cannot be opened (made on another PC, or damaged); nothing stored." : ex.Message);
            return 1;
        }
    }
}
