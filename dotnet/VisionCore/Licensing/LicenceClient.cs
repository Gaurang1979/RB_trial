using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionCore.Licensing;

/// <summary>A device of this installation sent for verification: kind (camera, plc, printer ...) and serial.</summary>
public sealed record DeviceReport(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("serial")] string Serial);

/// <summary>
/// Asks the licence server (an ERPNext method on your licence subdomain) to verify the licence key and
/// the devices of this installation. Only the licence key, installation id, software version, device
/// serials and a nonce are sent; nothing else from the PC.
/// </summary>
public sealed class LicenceClient
{
    private readonly HttpClient _http;

    public LicenceClient(HttpClient http) => _http = http;

    /// <summary>Path of the method on the ERPNext site.</summary>
    public const string Method = "/api/method/vision_licence.api.verify";

    public async Task<SignedLicence> VerifyAsync(string serverUrl, string licenceKey, string installation, string version,
                                                 IReadOnlyList<DeviceReport> devices, string nonce, CancellationToken token,
                                                 string? installationPublicKeyPem = null)
    {
        var url = serverUrl.TrimEnd('/') + Method;
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The licence server address must start with https://");
        // a plain JSON body with its length (not chunked): accepted by every web server and proxy.
        // installation_pubkey lets the server wrap the content key for this PC (LICENSING.md 7.4); empty until built.
        var body = JsonSerializer.Serialize(new
        {
            licence_key = licenceKey, installation, version, nonce,
            devices = JsonSerializer.Serialize(devices),   // Frappe passes values to the method as strings
            installation_pubkey = installationPublicKeyPem ?? "",
        });
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(url, content, token);
        resp.EnsureSuccessStatusCode();
        // Frappe wraps a method's return value in {"message": ...}
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync(token));
        var msg = doc.RootElement.GetProperty("message");
        return msg.Deserialize<SignedLicence>() ?? throw new InvalidOperationException("Empty answer from the licence server");
    }
}
