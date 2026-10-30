using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// What an admission froze for one firing, stored as the <c>sys.firing</c> checkpoint's JSON value: the firing's
/// id, how it was admitted, and every contributing schedule with the occurrence it selected and the timing it was
/// evaluated under. Immutable while the firing is outstanding; the checkpoint's status is the settlement. Pure ASCII
/// by construction (names are dotted kebab, the rest are ids and instants), which is what lets the SQL store it as
/// UTF-8 bytes without a conversion step.
/// </summary>
internal sealed record FiringEnvelope(Guid FiringId, string Kind, DateTime AdmittedAtUtc, IReadOnlyList<FiringContributor> Contributors)
{
    public const string ScheduledKind = "scheduled";

    public const string ManualKind = "manual";

    public string ToJson() => JsonSerializer.Serialize(this, FiringEnvelopeJsonContext.Default.FiringEnvelope);

    public static FiringEnvelope? FromJson(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize(utf8, FiringEnvelopeJsonContext.Default.FiringEnvelope);

    /// <summary>The contributors as the handler sees them, in name order; derived, so never stored.</summary>
    [JsonIgnore]
    public IReadOnlyList<TriggeringSchedule> TriggeringSchedules =>
        [.. Contributors.Select(c => new TriggeringSchedule(c.ScheduleName, c.OccurrenceAtUtc, c.TimeZoneId, c.CoveredThroughUtc))];
}

/// <summary>One schedule's share of a firing: the occurrence it selected and the timing it was evaluated under.</summary>
internal sealed record FiringContributor(
    int ScheduleId,
    string ScheduleName,
    DateTime OccurrenceAtUtc,
    string TimeZoneId,
    DateTime CoveredThroughUtc,
    string TimingFingerprint
);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FiringEnvelope))]
internal sealed partial class FiringEnvelopeJsonContext : JsonSerializerContext;
