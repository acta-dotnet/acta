using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// What an admission froze for one firing, stored as the <c>sys.firing</c> checkpoint's JSON value: the firing's
/// id, how it was admitted, and every contributing schedule as the handler reads it, in name order. Immutable while
/// the firing is outstanding; the checkpoint's status is the settlement. Pure ASCII by construction (names are dotted
/// kebab, the rest are ids and instants), which is what lets the SQL store it as UTF-8 bytes without a conversion step.
/// </summary>
internal sealed record FiringEnvelope(Guid FiringId, string Kind, DateTime AdmittedAtUtc, IReadOnlyList<TriggeringSchedule> Contributors)
{
    public const string ScheduledKind = "scheduled";

    public const string ManualKind = "manual";

    public string ToJson() => JsonSerializer.Serialize(this, FiringEnvelopeJsonContext.Default.FiringEnvelope);

    public static FiringEnvelope? FromJson(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize(utf8, FiringEnvelopeJsonContext.Default.FiringEnvelope);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FiringEnvelope))]
internal sealed partial class FiringEnvelopeJsonContext : JsonSerializerContext;
