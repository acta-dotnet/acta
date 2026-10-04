using System.Text.Json.Serialization;

namespace Acta.Runtime;

/// <summary>
/// The two fixed signal names of the operator-command inbox on a <c>sys.outbox</c> slot. Fixed names
/// bound the inbox: at most two command rows per source ever exist, because the source-to-slot mapping
/// is 1:1 (<see cref="OutboxRelayRegistry"/> keys registrations by namespace) and the applying tick
/// consumes each row in the same pass, so a healthy relay's inbox rests at zero.
/// </summary>
internal static class OutboxSignalNames
{
    public const string Requeue = "outbox.requeue";

    public const string Discard = "outbox.discard";
}

/// <summary>
/// One parked operator command, JSON-encoded into the signal checkpoint's value. <c>CommandId</c> is
/// minted per park; its uniqueness is what lets the admission statement distinguish "my write landed"
/// from "a concurrent command landed" by comparing the stored value. A null <c>OutboxIds</c> targets
/// every quarantined row of the source.
/// </summary>
internal sealed record OutboxSignalPayload(Guid CommandId, string? ActorKey, string? ReasonMessage, IReadOnlyList<Guid>? OutboxIds);

[JsonSerializable(typeof(OutboxSignalPayload))]
internal sealed partial class OutboxSignalJsonContext : JsonSerializerContext;

/// <summary>
/// Park admission outcome: <c>Action</c> uses the <c>ControlAction</c> ids (1 = the command is now the
/// pending one, 3 = rejected while another is pending), and <c>PendingSinceUtc</c> is the pending
/// command's park instant - the age the rejection reports to the operator.
/// </summary>
[DbProjection]
internal sealed record OutboxSignalAdmissionRow(byte Action, DateTime? PendingSinceUtc);

/// <summary>One pending command row as the applying tick reads it; <c>Version</c> feeds the consume CAS.</summary>
[DbProjection]
internal sealed record OutboxSignalRow(byte ValueFormatId, byte[] Value, int Version);

/// <summary>The consume outcome projection: how many rows the version-CAS delete removed (0 or 1).</summary>
[DbProjection]
internal sealed record OutboxSignalConsumeRow(long Consumed);
