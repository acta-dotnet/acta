using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// The <c>sys.outbox</c> operator-command inbox: Signal-kind checkpoint rows on the slot job, durable,
/// cross-peer, and bounded by the two fixed names. Operators park commands; the relay tick reads, applies,
/// records, and consumes them.
/// </summary>
internal sealed class OutboxInbox(
    IJobs jobs,
    IActaClock clock,
    IOptions<JobsOptions> options,
    ParkOutboxSignalHandler park,
    GetOutboxSignalHandler get,
    ConsumeOutboxSignalHandler consume,
    RecordOutboxEventHandler record
)
{
    /// <summary>
    /// Parks one command under <paramref name="name"/> for the namespace's slot: Accepted when it is now the
    /// pending one, Rejected with the pending command's park instant while another is unapplied, NotFound when
    /// the namespace has no slot.
    /// </summary>
    public async ValueTask<(ControlAction Action, DateTime? PendingSinceUtc)> ParkAsync(
        string name,
        string jobNamespace,
        IReadOnlyList<Guid>? outboxIds,
        string? reasonMessage,
        string? actorKey,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(jobNamespace);
        jobNamespace = IdentifierSyntax.CanonicalizeKebab(jobNamespace, nameof(jobNamespace));
        if (outboxIds is { Count: 0 })
        {
            throw new ArgumentException("OutboxIds must be null (every quarantined row) or non-empty.", nameof(outboxIds));
        }

        if (await OutboxSlot.FindAsync(jobs, jobNamespace, ct) is not { } slot)
        {
            return (ControlAction.NotFound, null);
        }

        // The minted CommandId is what distinguishes "my park landed" from "a concurrent command landed" in
        // the admission read; the actor rides the payload so the applying tick can stamp the evidence event
        // with the operator captured at park time. This is the only operator path that stamps its actor
        // without passing through JobControlActor, so its blank-means-unknown fold is repeated here or the
        // event carries whitespace where every sibling stores NULL.
        var payload = new OutboxSignalPayload(
            Guid.NewGuid(),
            string.IsNullOrWhiteSpace(actorKey) ? null : actorKey.Truncate(ActaTextLimits.ActorKey),
            reasonMessage.Truncate(ActaTextLimits.ReasonMessage),
            outboxIds
        );
        var nowUtc = await clock.GetUtcNowAsync(ct);
        var admission = await park.HandleAsync(
            new ParkOutboxSignalCommand(
                slot.JobId,
                name,
                JobPayloadFormat.Json.Id,
                JsonSerializer.SerializeToUtf8Bytes(payload, OutboxSignalJsonContext.Default.OutboxSignalPayload),
                StaleBeforeUtc: nowUtc - options.Value.WorkerDeadAfter
            ),
            ct
        );

        return admission.Action == (byte)ControlAction.Applied
            ? (ControlAction.Accepted, null)
            : (ControlAction.Rejected, admission.PendingSinceUtc);
    }

    public async Task<CheckpointValue?> GetAsync(long slotJobId, string name, CancellationToken ct) =>
        (await get.HandleAsync(new GetOutboxSignalQuery(slotJobId, name), ct))?.Signal;

    public Task<ConsumeOutboxSignalResponse> ConsumeAsync(long slotJobId, string name, int version, CancellationToken ct) =>
        consume.HandleAsync(new ConsumeOutboxSignalCommand(slotJobId, name, version), ct);

    public Task RecordAppliedAsync(RecordOutboxEventCommand command, CancellationToken ct) => record.HandleAsync(command, ct);
}
