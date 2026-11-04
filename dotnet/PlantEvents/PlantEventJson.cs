using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlantEvents;

/// <summary>
/// The wire form of plant events: JSON with snake_case names, the kind
/// first. Reading does not depend on property order, so any producer may
/// write the kind anywhere in the object.
/// </summary>
public static class PlantEventJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Names only: a number for an enum is refused, not read as a value.
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
    };

    static readonly Dictionary<string, Type> Kinds = new()
    {
        [InspectionResult.KindName] = typeof(InspectionResult),
        [LineState.KindName] = typeof(LineState),
        [CounterSnapshot.KindName] = typeof(CounterSnapshot),
        [AlarmRaised.KindName] = typeof(AlarmRaised),
        [AlarmCleared.KindName] = typeof(AlarmCleared),
        [AlarmAcknowledged.KindName] = typeof(AlarmAcknowledged),
        [JobChanged.KindName] = typeof(JobChanged),
        [ModelDeployed.KindName] = typeof(ModelDeployed),
        [HealthSample.KindName] = typeof(HealthSample),
        [Heartbeat.KindName] = typeof(Heartbeat),
    };

    /// <summary>Every kind of schema v1.</summary>
    public static IReadOnlyCollection<string> KnownKinds => Kinds.Keys;

    public static string Serialize(PlantEvent e) => JsonSerializer.Serialize(e, e.GetType(), Options);

    /// <summary>
    /// Reads one event. Throws <see cref="PlantEventException"/> for JSON that
    /// is not an object, an unknown kind, or a schema this code does not know.
    /// It does not validate the fields: call <see cref="PlantEvent.Validate"/>.
    /// </summary>
    public static PlantEvent Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new PlantEventException("not JSON: " + ex.Message); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new PlantEventException("not a JSON object");
            if (!root.TryGetProperty("kind", out var k) || k.ValueKind != JsonValueKind.String)
                throw new PlantEventException("no kind");
            var kind = k.GetString()!;
            if (!Kinds.TryGetValue(kind, out var type)) throw new PlantEventException($"unknown kind '{kind}'");
            if (!root.TryGetProperty("schema", out var s) || s.ValueKind != JsonValueKind.Number || !s.TryGetInt32(out var schema))
                throw new PlantEventException("no schema");
            if (schema != PlantEvent.CurrentSchema)
                throw new PlantEventException($"schema {schema} is not supported (this reads {PlantEvent.CurrentSchema})");
            try
            {
                return (PlantEvent)(root.Deserialize(type, Options) ?? throw new PlantEventException("empty event"));
            }
            catch (JsonException ex) { throw new PlantEventException($"bad {kind}: {ex.Message}"); }
        }
    }
}

public sealed class PlantEventException(string message) : Exception(message);
