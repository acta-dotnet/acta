using System.Data.Common;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// <see cref="IJobs.EnqueueAsync(EnqueueJobRequest, CancellationToken)"/>: prepares the row, allocates its public
/// ref in C#, and inserts it. An owned enqueue wakes the namespace; one in the caller's transaction never does,
/// since Acta cannot know whether or when the caller commits.
/// </summary>
internal sealed class EnqueueJobHandler(
    IDbSession session,
    ISqlDialect dialect,
    WorkerWakeupPublisher wakeup,
    IOptions<JobsOptions> options
)
{
    private readonly int _maxInlinePayloadBytes = options.Value.MaxInlinePayloadBytes;

    public async ValueTask<EnqueueJobResponse> HandleAsync(EnqueueJobCommand command, CancellationToken ct)
    {
        var request = command.Request;
        (request, var row) = JobEnqueue.Prepare(request, nameof(request), 0, _maxInlinePayloadBytes);
        var jobRef = JobRef.New().Value;

        var rows = await JobEnqueue.ExecuteAsync(() => InsertAsync(row, jobRef, command.Transaction, ct));
        if (rows.Count != 1)
        {
            throw new InvalidOperationException(
                $"Enqueue returned {rows.Count} outcomes for one input row. The enqueue_job routine must produce exactly one outcome."
            );
        }

        var outcome = new EnqueueJobResponse(rows[0].JobId, new JobRef(rows[0].JobRef), rows[0].Action);
        if (command.Transaction is null && JobEnqueue.WakeReason(request, outcome.Action) is { } reason)
        {
            await wakeup.WakeAsync(WorkerWakeupChannel.WorkerNamespace(request.JobNamespace), reason, ct);
        }

        return outcome;
    }

    /// <summary>One <c>enqueue_job</c> call with the allocated ref, through <paramref name="transaction"/> when it is set.</summary>
    internal Task<IReadOnlyList<EnqueueJobRow>> InsertAsync(
        JobEnqueueValues row,
        Guid jobRef,
        DbTransaction? transaction,
        CancellationToken ct
    )
    {
        var command = new StoreCommand("EnqueueJob");
        var map = DbProjectionResolver.Resolve<EnqueueJobRow>();
        return transaction is null
            ? session.ExecuteAsync(command, Bind, map, ct)
            : session.ExecuteInTransactionAsync(transaction, command, Bind, map, ct);

        void Bind(DbCommand cmd) => dialect.BindEnqueueJob(cmd, row, jobRef, session.Schema);
    }
}
