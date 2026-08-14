using System.Text.Json.Serialization;

namespace Acta.Runtime.Modules.Outbox;

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
/// Park one operator command on the slot's signal checkpoint: insert when the slot is free, supersede
/// when the pending command is older than <paramref name="StaleBeforeUtc"/> (the worker-dead window -
/// a command nothing has consumed for that long has no live consumer racing the overwrite), reject
/// otherwise. Version-CAS on the checkpoint keeps overwrite-vs-apply races safe.
/// </summary>
internal sealed record ParkOutboxSignalCommand(long JobId, string Name, byte ValueFormatId, byte[] Value, DateTime StaleBeforeUtc);

/// <summary>
/// Park admission outcome: <c>Action</c> uses the <c>ControlAction</c> ids (1 = the command is now the
/// pending one, 3 = rejected while another is pending), and <c>PendingSinceUtc</c> is the pending
/// command's park instant - the age the rejection reports to the operator.
/// </summary>
internal sealed record OutboxSignalAdmissionRow(byte Action, DateTime? PendingSinceUtc);

/// <summary>One pending command row as the applying tick reads it; <c>Version</c> feeds the consume CAS.</summary>
internal sealed record OutboxSignalRow(byte ValueFormatId, byte[] Value, int Version);

/// <summary>The consume outcome projection: how many rows the version-CAS delete removed (0 or 1).</summary>
internal sealed record OutboxSignalConsumeRow(long Consumed);

/// <summary>
/// Evidence event for an applied command, written against the slot job before the command row is
/// consumed: the operator actor and a reason message carrying the operator's justification plus the
/// applied row ids, so proof of the action outlives the (possibly deleted) source rows.
/// </summary>
internal sealed record RecordOutboxEventCommand(long JobId, EventCode EventCode, string? ActorKey, string? ReasonMessage);

/// <summary>
/// Ledger-side persistence port for the sys.outbox operator-command inbox (distinct from
/// <see cref="IOutboxRelayStore"/>, which is the SOURCE-database port). Commands live as Signal-kind
/// checkpoint rows on the slot job: durable, cross-peer, and bounded by the two fixed names.
/// </summary>
internal interface IOutboxSignalStore
{
    /// <summary>
    /// Single-statement park admission: insert when free, supersede when the pending row is stale,
    /// reject otherwise. Never blocks on the applying tick; the version bump on supersede is what makes
    /// a concurrent consume of the old command miss its CAS.
    /// </summary>
    Task<OutboxSignalAdmissionRow> ParkAsync(ParkOutboxSignalCommand command, CancellationToken ct);

    /// <summary>Reads the pending command row under <paramref name="name"/>, or null when the inbox slot is empty.</summary>
    Task<OutboxSignalRow?> GetAsync(long jobId, string name, CancellationToken ct);

    /// <summary>
    /// Version-CAS delete of an applied command row. False means a newer command superseded it while it
    /// was being applied; the newer command simply applies on the next tick.
    /// </summary>
    Task<bool> ConsumeAsync(long jobId, string name, int version, CancellationToken ct);

    /// <summary>Appends the applied-command evidence event against the slot job (always audited).</summary>
    Task RecordAppliedAsync(RecordOutboxEventCommand command, CancellationToken ct);
}
