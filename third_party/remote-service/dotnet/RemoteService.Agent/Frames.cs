using System.Text;
using System.Text.Json;

namespace Sundaram.RemoteService;

/// <summary>How the agent reaches the relay and the host's own web pages.</summary>
public sealed class RemoteServiceOptions
{
    /// <summary>The relay's link address, e.g. wss://support.sundaramtech.com/link.</summary>
    public string Relay { get; set; } = "";
    /// <summary>This machine's number on the relay.</summary>
    public string Machine { get; set; } = "";
    /// <summary>The link token printed by the relay; the host keeps it in its protected secret store, never in a settings file.</summary>
    public string? Token { get; set; }
    /// <summary>The host's local web pages to forward to (https://127.0.0.1:8443), or null for none.</summary>
    public Uri? LocalWeb { get; set; }
    /// <summary>SHA-256 of the local web pages' TLS certificate: trusted exactly, whatever name it carries.</summary>
    public byte[]? LocalWebCertificateSha256 { get; set; }
    /// <summary>Where the agent keeps what it must remember across restarts: the package versions installed (rs-state.json) and
    /// log entries the relay has not confirmed yet (rs-log.json). A folder only the host's service account can write. Without
    /// it the agent takes no software packages (it could not refuse an older one after a restart).</summary>
    public string? DataFolder { get; set; }
    /// <summary>Plain ws:// and http:// allowed: tests and a developer's PC only.</summary>
    public bool AllowInsecure { get; set; }
    /// <summary>A session ends after this long without a request.</summary>
    public TimeSpan SessionIdle { get; set; } = TimeSpan.FromMinutes(15);
    /// <summary>A session ends after this long whatever happens.</summary>
    public TimeSpan SessionMax { get; set; } = TimeSpan.FromHours(8);
    /// <summary>Response headers of the host's pages, besides the standard ones, passed back to the browser ("X-Inspection-Id").</summary>
    public IReadOnlyList<string> ExtraReturnHeaders { get; set; } = [];

    /// <summary>Why the agent cannot run with these options (never the token), or null.</summary>
    public string? Problem()
    {
        if (string.IsNullOrWhiteSpace(Relay)) return "no relay address";
        if (!Uri.TryCreate(Relay, UriKind.Absolute, out var u) || u.Scheme is not ("wss" or "ws")) return "the relay address must be wss://";
        if (u.Scheme == "ws" && !AllowInsecure) return "the relay address must be wss:// (TLS)";
        if (!MachineId.IsValid(Machine)) return "the machine number must be 2-41 letters, digits, '_' or '-'";
        if (string.IsNullOrEmpty(Token) || Token.Length < 32) return "no link token (at least 32 characters)";
        if (LocalWeb is { } w && w.Scheme == "http" && !AllowInsecure) return "the local web pages must be https";
        if (LocalWeb is { } h && !(h.IsLoopback)) return "the local web pages must be on this PC (127.0.0.1)";
        return null;
    }
}

public static class MachineId
{
    public static bool IsValid(string? id) =>
        id is { Length: >= 2 and <= 41 } && char.IsAsciiLetterOrDigit(id[0]) && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}

/// <summary>One browser request forwarded by the relay (PROTOCOL.md section 2).</summary>
public sealed record RemoteRequest(string Id, string Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers,
    string User, RemoteRole Role, byte[] Body)
{
    public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var v) ? v : null;

    public string? Cookie(string name)
    {
        foreach (var part in (Header("cookie") ?? "").Split(';'))
        {
            var p = part.Trim();
            if (p.StartsWith(name + "=", StringComparison.Ordinal)) return p[(name.Length + 1)..];
        }
        return null;
    }

    /// <summary>The query's fields, decoded; a field given twice keeps the first.</summary>
    public IReadOnlyDictionary<string, string> QueryFields() => Form(Query.TrimStart('?'));

    /// <summary>The body as a form (application/x-www-form-urlencoded).</summary>
    public IReadOnlyDictionary<string, string> FormFields() => Form(Encoding.UTF8.GetString(Body));

    static Dictionary<string, string> Form(string s)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in s.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            var k = Uri.UnescapeDataString((i < 0 ? pair : pair[..i]).Replace('+', ' '));
            var v = i < 0 ? "" : Uri.UnescapeDataString(pair[(i + 1)..].Replace('+', ' '));
            d.TryAdd(k, v);
        }
        return d;
    }
}

/// <summary>Writes one answer as head, data and end frames (PROTOCOL.md section 3).</summary>
public sealed class RemoteResponse(string id, Func<object, Task> send)
{
    public const int Chunk = 32 << 10;
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    bool _head, _ended;

    public bool Started => _head;

    public async Task Head(int status, IDictionary<string, string[]>? headers = null)
    {
        if (_head) throw new InvalidOperationException("head sent twice");
        _head = true;
        await send(new { t = "head", id, status, headers = headers ?? new Dictionary<string, string[]>() });
    }

    public async Task Data(ReadOnlyMemory<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i += Chunk)
        {
            var part = bytes.Slice(i, Math.Min(Chunk, bytes.Length - i));
            await send(new { t = "data", id, b64 = Convert.ToBase64String(part.Span) });
        }
    }

    public async Task End(string? error = null)
    {
        if (_ended) return;
        _ended = true;
        await send(error is null ? new { t = "end", id } : new { t = "end", id, error });
    }

    /// <summary>A whole JSON answer.</summary>
    public async Task Json<T>(int status, T value, IDictionary<string, string[]>? headers = null)
    {
        var h = headers ?? new Dictionary<string, string[]>();
        h["Content-Type"] = ["application/json; charset=utf-8"];
        h.TryAdd("Cache-Control", ["no-store"]);
        await Head(status, h);
        await Data(JsonSerializer.SerializeToUtf8Bytes(value, Web));
        await End();
    }

    public Task Error(int status, string text, string key) => Json(status, new { error = text, key });
}
