using System.Text;
using Microsoft.Extensions.Options;
using VisionStudio.Remote.V1;
using VisionStudio.Services;
using VisionSystem.Runtime;

// vs-runtime [--console] [--Runtime:Endpoint=<socket path | pipe:name>] [--Studio:DataFolder=<folder>] ...
//   Runs the line and serves the runtime contract. Started by the Windows service manager it runs
//   as the service; otherwise (or with --console) as a plain program, stopped with Ctrl+C.
// vs-runtime add-user <name> "<full name>" <Operator,Supervisor,Engineer,Admin,QA> [--password <p>] [--pin <n>] [--card] [--lines "<line>,<line>"]
//   --lines limits the browser dashboard to these lines (plant id or report name); "" = every line.
//   --card asks for the badge: tap it on the reader (it types the number and Enter); nothing is shown.
// vs-runtime add-token <name> [--operate]
//   Adds a plant-software token and prints it once (only its hash is kept).
// vs-runtime self-test [--grab <n>]
//   Checks the engine DLLs, AI models, folders, cameras, job camera serials and port 502; with
//   --grab fires each job camera n times (stop the service first). Exit 0 = all passed.
if (args.Length >= 1 && args[0] is "add-user" or "add-token")
    return Commands.Run(args);
if (args.Length >= 1 && args[0] == "self-test")
    return SelfTest.Run(args);

var app = RuntimeHost.Build(args);
var endpoint = app.Services.GetRequiredService<IOptions<RuntimeOptions>>().Value.LocalEndpoint();
app.Lifetime.ApplicationStarted.Register(() => Console.WriteLine($"vs-runtime {LineRuntime.Version} on {endpoint}"));
await app.RunAsync();
return 0;

static class Commands
{
    public static int Run(string[] args)
    {
        var config = new ConfigurationBuilder();
        string? Opt(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        var rest = args.Skip(1).Where(a => a.StartsWith("--Studio:", StringComparison.Ordinal)).ToArray();
        RuntimeHost.Configure(config, rest, AppContext.BaseDirectory);
        var studio = config.Build().GetSection("Studio").Get<StudioOptions>() ?? new StudioOptions();
        studio.ResolvePaths(AppContext.BaseDirectory);
        var store = new AccessStore(studio.DataFolder);
        if (args[0] == "add-token")
        {
            if (args.Length < 2) { Console.Error.WriteLine("add-token <name> [--operate]"); return 2; }
            var token = store.AddToken(args[1], args.Contains("--operate"));
            Console.WriteLine($"token for {args[1]} (shown once, keep it in the plant software's secret store):");
            Console.WriteLine(token);
            return 0;
        }
        if (args.Length < 4) { Console.Error.WriteLine("add-user <name> \"<full name>\" <roles> [--password <p>] [--pin <n>] [--card] [--lines \"<line>,<line>\"]"); return 2; }
        var roles = new List<Role>();
        foreach (var r in args[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<Role>(r, ignoreCase: true, out var role) || role == Role.Unspecified) { Console.Error.WriteLine($"unknown role {r}"); return 2; }
            roles.Add(role);
        }
        var pin = Opt("--pin");
        if (pin is not null && (pin.Length < 4 || !pin.All(char.IsAsciiDigit))) { Console.Error.WriteLine("a PIN is 4 or more digits"); return 2; }
        string? card = null;
        if (args.Contains("--card"))
        {
            // read without echo, so the number never shows on the screen or in a shell history
            Console.Error.Write("Tap the card on the reader: ");
            var typed = new StringBuilder();
            if (Console.IsInputRedirected) typed.Append(Console.ReadLine());
            else
                for (var k = Console.ReadKey(intercept: true); k.Key != ConsoleKey.Enter; k = Console.ReadKey(intercept: true))
                    if (!char.IsControl(k.KeyChar)) typed.Append(k.KeyChar);
            Console.Error.WriteLine();
            card = typed.ToString();
            if (VisionStudio.Remote.RuntimeAccess.NormalizeCard(card).Length == 0) { Console.Error.WriteLine("no card read"); return 2; }
        }
        var lines = Opt("--lines")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        try { store.SaveUser(args[1], args[2], roles, Opt("--password"), pin, card, lines); }
        catch (InvalidOperationException e) { Console.Error.WriteLine(e.Message); return 2; }
        Console.WriteLine($"user {args[1]} saved in {studio.DataFolder}");
        return 0;
    }
}
