using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// The single-statement park admission. It never blocks on the applying tick; every park stores a fresh
/// command id, so a concurrent consume of the old command no longer matches the stored value.
/// </summary>
internal sealed class ParkOutboxSignalHandler(
    IDbSession session,
    ISqlDialect dialect,
    ResolveSlotIdHandler resolveSlotId,
    IJobs jobs,
    IActaClock clock,
    IOptions<JobsOptions> options,
    GetOutboxSignalHandler getSignal
)
{
    public Task<ParkOutboxSignalResponse> HandleAsync(ParkOutboxSignalCommand command, CancellationToken ct) =>
        session.ExecuteOneAsync(
            new StoreCommand("ParkOutboxSignal"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.CheckpointName, command.Name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.ValueFormatId, command.ValueFormatId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.Value, command.Value));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StaleBefore, command.StaleBeforeUtc));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ReplaceableValue, command.ReplaceableValue));
            },
            DbProjectionResolver.Resolve<ParkOutboxSignalResponse>(),
            ct
        );

    /// <summary>
    /// Parks one operator command under <paramref name="name"/> for the namespace's slot: Accepted when it is now the
    /// pending one, Rejected with the pending command's park instant while another is unapplied, NotFound when
    /// the namespace has no slot.
    /// </summary>
    public async ValueTask<(ControlAction Action, DateTime? PendingSinceUtc)> ParkAsync(
        string name,
        string jobNamespace,
        IReadOnlyList<Guid>? outboxIds,
        string? reasonMessage,
        string? actor,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(jobNamespace);
        jobNamespace = NameSyntax.Canonicalize(jobNamespace, nameof(jobNamespace));
        if (outboxIds is { Count: 0 })
        {
            throw new ArgumentException("OutboxIds must be null (every quarantined row) or non-empty.", nameof(outboxIds));
        }

        if (await OutboxSlot.FindAsync(resolveSlotId, jobs, jobNamespace, ct) is not { } slot)
        {
            return (ControlAction.NotFound, null);
        }

        // The minted CommandId is what distinguishes "my park landed" from "a concurrent command landed" in
        // the admission read; the actor rides the payload so the applying tick can stamp the evidence event
        // with the operator captured at park time. The actor is validated here, at park time, because the
        // applying tick only carries it.
        var payload = new OutboxSignalPayload(
            Guid.NewGuid(),
            JobControlActor.Operator(actor),
            reasonMessage.Truncate(ActaTextLimits.EventMessage),
            outboxIds
        );
        var nowUtc = await clock.GetUtcNowAsync(ct);
        var admission = await AdmitAsync(
            slot.JobId,
            name,
            JsonSerializer.SerializeToUtf8Bytes(payload, OutboxSignalJsonContext.Default.OutboxSignalPayload),
            nowUtc - options.Value.WorkerDeadAfter,
            ct
        );

        return admission.Action == (byte)ControlAction.Applied
            ? (ControlAction.Accepted, null)
            : (ControlAction.Rejected, admission.PendingSinceUtc);
    }

    /// <summary>
    /// Admits one serialized command. A stale pending command is replaced only while it is still the unplanned one this
    /// read found: a planned one may already have changed the source, and the next relay pass replays its plan and
    /// writes its evidence, which a replacement would erase.
    /// </summary>
    internal async Task<ParkOutboxSignalResponse> AdmitAsync(
        long slotJobId,
        string name,
        byte[] value,
        DateTime staleBeforeUtc,
        CancellationToken ct
    )
    {
        var pending = (await getSignal.HandleAsync(new GetOutboxSignalQuery(slotJobId, name), ct))?.Signal.Value;
        return await HandleAsync(
            new ParkOutboxSignalCommand(
                slotJobId,
                name,
                JobPayloadFormat.Json.Id,
                value,
                staleBeforeUtc,
                ReplaceableValue: pending is not null && !IsPlanned(pending) ? pending : null
            ),
            ct
        );
    }

    // A malformed command changed nothing, and the relay consumes it unapplied, so it may be replaced.
    private static bool IsPlanned(byte[] value)
    {
        try
        {
            return JsonSerializer.Deserialize(value, OutboxSignalJsonContext.Default.OutboxSignalPayload)?.PlannedIds is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
