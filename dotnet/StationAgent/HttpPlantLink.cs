using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace StationAgent;

/// <summary>
/// The station's link to the plant server over HTTPS: events go as one JSON
/// array to /vision/ingest/v1/events, heartbeats to /vision/ingest/v1/heartbeat (the plant
/// server's Vision module, plant_server/backend/vision), each with
/// the station's token. Any answer other than success throws, so the agent
/// keeps the events and tries again.
/// </summary>
public sealed class HttpPlantLink : IPlantLink
{
    /// <summary>The most one request carries; a longer batch is sent in part (the contract allows it).</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    readonly HttpClient _http;

    /// <param name="http">A client whose BaseAddress is the plant server.</param>
    /// <param name="token">The station's token from "PlantServer add-station".</param>
    /// <param name="allowHttp">Only for tests and a server on the same PC: the token travels in every request, so the plant network gets https.</param>
    public HttpPlantLink(HttpClient http, string token, bool allowHttp = false)
    {
        if (http.BaseAddress is null) throw new ArgumentException("the client needs the plant server's address", nameof(http));
        if (http.BaseAddress.Scheme != Uri.UriSchemeHttps && !allowHttp)
            throw new ArgumentException("the plant server's address must be https://", nameof(http));
        _http = http;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<long> SendAsync(IReadOnlyList<QueuedEvent> batch, CancellationToken ct)
    {
        // The stored JSON goes as it is: what the station validated is what the server reads.
        // Up to MaxBytes, but always at least one event so the queue moves.
        var body = new StringBuilder().Append('[');
        var bytes = 2;
        for (var i = 0; i < batch.Count; i++)
        {
            var n = Encoding.UTF8.GetByteCount(batch[i].Json) + 1;
            if (i > 0 && bytes + n > MaxBytes) break;
            if (i > 0) body.Append(',');
            body.Append(batch[i].Json);
            bytes += n;
        }
        body.Append(']');
        using var content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
        using var r = await _http.PostAsync("vision/ingest/v1/events", content, ct);
        var text = await r.Content.ReadAsStringAsync(ct);
        if (!r.IsSuccessStatusCode) throw new HttpRequestException($"plant server answered {(int)r.StatusCode}: {text}");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("stored_up_to").GetInt64();
    }

    /// <summary>The most image ids one heartbeat answer may ask for (the contract); more are ignored.</summary>
    public const int MaxImageRequests = 10;

    public async Task<HeartbeatAnswer> SendHeartbeatAsync(string json, CancellationToken ct)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var r = await _http.PostAsync("vision/ingest/v1/heartbeat", content, ct);
        var text = await r.Content.ReadAsStringAsync(ct);
        if (!r.IsSuccessStatusCode) throw new HttpRequestException($"plant server answered {(int)r.StatusCode}: {text}");
        return new HeartbeatAnswer(ParseImageRequests(text), ParseChangeover(text));
    }

    /// <summary>
    /// The "changeover" of a heartbeat answer, or null when it is missing or not the contract's shape (an older
    /// server, a broken answer): the station then keeps what it knew, and a check it cannot see stays undone.
    /// </summary>
    public static ChangeoverAnswer? ParseChangeover(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("changeover", out var c) || c.ValueKind != JsonValueKind.Object) return null;
            if (!c.TryGetProperty("required", out var req) || req.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            if (!c.TryGetProperty("state", out var st) || st.GetString() is not ({ } state and (ChangeoverAnswer.Pending or ChangeoverAnswer.Accepted
                    or ChangeoverAnswer.Refused or ChangeoverAnswer.NotNeeded))) return null;
            var code = c.TryGetProperty("code", out var cd) && cd.ValueKind == JsonValueKind.String ? cd.GetString()! : "";
            var ask = c.TryGetProperty("ask", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out var n) ? n : 0;
            string? Text(string name) => c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            long? at = c.TryGetProperty("at", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var ms) ? ms : null;
            return new ChangeoverAnswer(req.GetBoolean(), code, ask, state, Text("by"), at, Text("note"));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// The "image_requests" of a heartbeat answer: strings only, up to <see cref="MaxImageRequests"/>, in order.
    /// An old server's answer without them, a body that is not JSON, or any other shape gives an empty list.
    /// </summary>
    public static IReadOnlyList<string> ParseImageRequests(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return [];
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("image_requests", out var a) || a.ValueKind != JsonValueKind.Array) return [];
            var ids = new List<string>();
            foreach (var e in a.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 and <= 128 } id && !ids.Contains(id) && ids.Count < MaxImageRequests)
                    ids.Add(id);
            return ids;
        }
        catch (JsonException) { return []; }
    }

    public async Task UploadImageAsync(string imageId, string camera, byte[] jpeg, CancellationToken ct)
    {
        using var content = new ByteArrayContent(jpeg);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        using var r = await _http.PostAsync($"vision/ingest/v1/images/{Uri.EscapeDataString(imageId)}?camera={Uri.EscapeDataString(camera)}", content, ct);
        await EnsureOk(r, ct);
    }

    public async Task ImageDoneAsync(string imageId, CancellationToken ct)
    {
        using var r = await _http.PostAsync($"vision/ingest/v1/images/{Uri.EscapeDataString(imageId)}/done", null, ct);
        await EnsureOk(r, ct);
    }

    public async Task ImageMissingAsync(string imageId, CancellationToken ct)
    {
        using var r = await _http.PostAsync($"vision/ingest/v1/images/{Uri.EscapeDataString(imageId)}/missing", null, ct);
        await EnsureOk(r, ct);
    }

    static async Task EnsureOk(HttpResponseMessage r, CancellationToken ct)
    {
        // The status travels with the exception (HttpRequestException.StatusCode): the agent treats 400 and 413 as final.
        if (!r.IsSuccessStatusCode) throw new HttpRequestException($"plant server answered {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync(ct)}", null, r.StatusCode);
    }
}
