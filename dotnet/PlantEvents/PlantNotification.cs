using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlantEvents;

public enum NotificationCategory
{
    HardwareOptics,
    PrinterDiagnostic,
    MotionEncoder,
    SubstrateApplicator,
    CodeIntegrity,
    SecurityTamper,
    Custom
}

public enum NotificationSeverity
{
    Info,
    Warning,
    Critical
}

public enum PrinterType
{
    None,
    TTO,
    CIJ,
    TIJ
}

/// <summary>
/// A unified notification envelope for hardware, software, printer and motion diagnostics.
/// Reusable across Vision System, Track & Trace, cartoner PLCs, and third-party packaging modules
/// publishing over the common IPC bus (Named Pipe / Unix Domain Socket / REST).
/// Schema v1 per REQ-18 and PROT-33.
/// </summary>
public sealed record PlantNotification
{
    public const string DefaultPipeName = "SundaramNotificationBus";
    public const string DefaultSocketName = "sundaram_notif_bus.sock";
    public const int DefaultRestPort = 52199;

    [JsonPropertyOrder(-10)]
    public string Id { get; init; } = "";

    [JsonPropertyOrder(-9)]
    public long Ts { get; init; }

    [JsonPropertyOrder(-8)]
    public string SourceProcess { get; init; } = "VisionCore.Engine";

    [JsonPropertyOrder(-7)]
    public string Plant { get; init; } = "";

    [JsonPropertyOrder(-6)]
    public string Line { get; init; } = "";

    [JsonPropertyOrder(-5)]
    public string Station { get; init; } = "";

    [JsonPropertyOrder(-4)]
    public string? Camera { get; init; }

    [JsonPropertyOrder(-3)]
    public NotificationCategory Category { get; init; } = NotificationCategory.HardwareOptics;

    [JsonPropertyOrder(-2)]
    public NotificationSeverity Severity { get; init; } = NotificationSeverity.Warning;

    [JsonPropertyOrder(-1)]
    public PrinterType Printer { get; init; } = PrinterType.None;

    public string Code { get; init; } = "";
    public string Title { get; init; } = "";
    public string MessageEn { get; init; } = "";
    public IReadOnlyDictionary<string, string> LocalizedGuidance { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, double> Metrics { get; init; } = new Dictionary<string, double>();
    public string? RecommendedAction { get; init; }
    public bool AckRequired { get; init; } = true;
    public string State { get; init; } = "Active";
    public string? AcknowledgedBy { get; init; }
    public long? AcknowledgedAt { get; init; }

    public IReadOnlyList<string> Validate()
    {
        var p = new List<string>();
        if (string.IsNullOrWhiteSpace(Id)) p.Add("id is empty");
        if (Ts <= 0) p.Add("ts is not set");
        if (string.IsNullOrWhiteSpace(SourceProcess)) p.Add("source_process is empty");
        if (string.IsNullOrWhiteSpace(Plant)) p.Add("plant is empty");
        if (string.IsNullOrWhiteSpace(Line)) p.Add("line is empty");
        if (string.IsNullOrWhiteSpace(Station)) p.Add("station is empty");
        if (string.IsNullOrWhiteSpace(Code)) p.Add("code is empty");
        if (string.IsNullOrWhiteSpace(Title)) p.Add("title is empty");
        return p;
    }
}

public static class PlantNotificationJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
    };

    public static string Serialize(PlantNotification n) => JsonSerializer.Serialize(n, Options);

    public static PlantNotification Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PlantNotification>(json, Options)
                ?? throw new JsonException("Deserialized notification is null");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid plant notification JSON: {ex.Message}", ex);
        }
    }
}
