using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// The <c>sys.alerts</c> projection cursor as one value: the <c>created_at_utc</c> ticks and the id
/// of the last event consumed, or the horizon a short batch advanced the instant to. One variable
/// holds both halves so a checkpoint is one write and two writers can never interleave the halves
/// of different checkpoints.
/// </summary>
internal sealed record AlertsCursor(long Ticks, long EventId)
{
    [JsonIgnore]
    public DateTime Utc => new(Ticks, DateTimeKind.Utc);
}

/// <summary>
/// Source-generated metadata for the types Acta's own system jobs serialize through the job payload
/// serializer, currently the <c>sys.alerts</c> cursor (<c>alerts-cursor</c>). Chained after the
/// app-supplied resolver in <see cref="JsonJobPayloadSerializer"/> so, under reflection-off Native
/// AOT, a system job never fails for a type the consuming app had no reason to register.
/// App-registered types take precedence; this only backfills the framework's own.
/// </summary>
[JsonSerializable(typeof(AlertsCursor))]
internal sealed partial class ActaSystemJobJsonContext : JsonSerializerContext;
