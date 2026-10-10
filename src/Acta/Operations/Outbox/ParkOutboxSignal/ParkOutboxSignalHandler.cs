using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// The single-statement park admission. It never blocks on the applying tick; the version bump on a
/// supersede is what makes a concurrent consume of the old command miss its CAS.
/// </summary>
internal sealed class ParkOutboxSignalHandler(
    IDbSession session,
    ISqlDialect dialect,
    IJobs jobs,
    IActaClock clock,
    IOptions<JobsOptions> options
)
{
    public Task<ParkOutboxSignalResponse> HandleAsync(ParkOutboxSignalCommand command, CancellationToken ct) =>
        session.ExecuteOneAsync(
            new StoreCommand("ParkOutboxSignal"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.Name, command.Name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.ValueFormatId, command.ValueFormatId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.Value, command.Value));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StaleBefore, command.StaleBeforeUtc));
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
        var admission = await HandleAsync(
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
}
