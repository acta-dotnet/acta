using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace Acta.Runtime;

/// <summary>
/// <see cref="IJobs.EnqueueBatchAsync(IReadOnlyList{JobEnqueueRequest}, CancellationToken)"/>: prepares every row,
/// rejects same-batch duplicate keys, and inserts the batch in one statement. An owned batch wakes each due
/// namespace once; one in the caller's transaction never wakes.
/// </summary>
internal sealed class EnqueueBatchHandler(
    EnqueueOneHandler enqueueOne,
    IDbSession session,
    ISqlDialect dialect,
    WorkerWakeupPublisher wakeup,
    IOptions<JobsOptions> options
)
{
    private const int MaxRows = 5000;
    private const long MaxPayloadBytes = 64L * 1024 * 1024;

    private readonly int _maxInlinePayloadBytes = options.Value.MaxInlinePayloadBytes;

    public async ValueTask<IReadOnlyList<JobEnqueueResponse>> HandleAsync(EnqueueBatchCommand command, CancellationToken ct)
    {
        var requests = command.Requests;
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            return [];
        }

        // One row dispatches to the scalar path; the outcomes are contractually identical.
        if (requests.Count == 1)
        {
            return [await enqueueOne.HandleAsync(new EnqueueOneCommand(requests[0], command.Transaction), ct)];
        }

        var (prepared, rows) = Prepare(requests, _maxInlinePayloadBytes);

        // The public ref is allocated here, never by the database; the routine writes it onto the inserted row
        // and echoes it back, or returns the existing ref of a deduplicated row.
        var jobRefs = new Guid[rows.Length];
        for (var i = 0; i < jobRefs.Length; i++)
        {
            jobRefs[i] = JobRef.New().Value;
        }

        var outcomeRows = await JobEnqueue.ExecuteAsync(() => InsertAsync(rows, jobRefs, command.Transaction, ct));
        if (outcomeRows.Count != rows.Length)
        {
            throw new InvalidOperationException(
                $"Enqueue batch returned {outcomeRows.Count} outcomes for {rows.Length} input rows. "
                    + "The enqueue_batch routine must produce exactly one outcome per input ordinal."
            );
        }

        var outcomes = new JobEnqueueResponse[rows.Length];
        foreach (var row in outcomeRows)
        {
            outcomes[row.Ordinal] = new JobEnqueueResponse(row.JobId, new JobRef(row.JobRef), row.Action);
        }

        if (command.Transaction is null)
        {
            await WakeNamespacesAsync(prepared, outcomes, ct);
        }

        return outcomes;
    }

    /// <summary>
    /// One <c>enqueue_batch</c> call, through <paramref name="transaction"/> when it is set; the refs align with the
    /// rows by position and each outcome carries its input ordinal.
    /// </summary>
    internal Task<IReadOnlyList<EnqueueOutcomeRow>> InsertAsync(
        IReadOnlyList<JobEnqueueRow> rows,
        IReadOnlyList<Guid> jobRefs,
        DbTransaction? transaction,
        CancellationToken ct
    )
    {
        var command = new StoreCommand("EnqueueBatch");
        var map = DbProjectionResolver.Resolve<EnqueueOutcomeRow>();
        return transaction is null
            ? session.ExecuteAsync(command, Bind, map, ct)
            : session.ExecuteInTransactionAsync(transaction, command, Bind, map, ct);

        void Bind(DbCommand cmd) => dialect.BindEnqueueBatch(cmd, rows, jobRefs, session.Schema);
    }

    /// <summary>Prepares every row against the batch limits, then rejects same-batch duplicate keys.</summary>
    private static (JobEnqueueRequest[] Prepared, JobEnqueueRow[] Rows) Prepare(
        IReadOnlyList<JobEnqueueRequest> requests,
        int maxInlinePayloadBytes
    )
    {
        if (requests.Count > MaxRows)
        {
            throw new ArgumentException(
                $"Enqueue batch size {requests.Count} exceeds the {MaxRows}-row limit. Split the batch caller-side and retry.",
                nameof(requests)
            );
        }

        var prepared = new JobEnqueueRequest[requests.Count];
        var rows = new JobEnqueueRow[requests.Count];
        long payloadBytes = 0;
        for (var i = 0; i < requests.Count; i++)
        {
            (prepared[i], rows[i]) = JobEnqueue.Prepare(requests[i], $"{nameof(requests)}[{i}]", i, maxInlinePayloadBytes);
            payloadBytes += rows[i].Input.Data.Length;
        }

        if (payloadBytes > MaxPayloadBytes)
        {
            throw new ArgumentException(
                $"Enqueue batch carries {payloadBytes} payload bytes, over the {MaxPayloadBytes}-byte limit. "
                    + "Split the batch caller-side and retry.",
                nameof(requests)
            );
        }

        ValidateDeduplicationKeyUniqueness(rows);
        return (prepared, rows);
    }

    /// <summary>
    /// One wake per distinct namespace; a due-now row's WorkAvailable outranks a delayed row's HorizonChanged for
    /// the same namespace (the wake itself is identical; the reason is metrics-only).
    /// </summary>
    private async ValueTask WakeNamespacesAsync(JobEnqueueRequest[] requests, JobEnqueueResponse[] outcomes, CancellationToken ct)
    {
        Dictionary<string, WorkerWakeupReason>? wakes = null;
        for (var i = 0; i < outcomes.Length; i++)
        {
            var request = requests[i];
            if (JobEnqueue.WakeReason(request, outcomes[i].Action) is { } reason)
            {
                wakes ??= new Dictionary<string, WorkerWakeupReason>(StringComparer.Ordinal);
                if (!wakes.TryGetValue(request.JobNamespace, out var existing) || existing != WorkerWakeupReason.WorkAvailable)
                {
                    wakes[request.JobNamespace] = reason;
                }
            }
        }

        if (wakes is null)
        {
            return;
        }

        foreach (var (ns, reason) in wakes)
        {
            await wakeup.WakeAsync(WorkerWakeupChannel.WorkerNamespace(ns), reason, ct);
        }
    }

    /// <summary>
    /// Same-batch duplicate keys never reach the routine's dedup, which matches only rows already in the table,
    /// and the providers diverge on them (SQL Server trips the unique index, PostgreSQL skips one row), so they
    /// are rejected before any SQL. The scope mirrors the unique indexes: per namespace for roots, per parent for children.
    /// </summary>
    internal static void ValidateDeduplicationKeyUniqueness(IReadOnlyList<JobEnqueueRow> rows)
    {
        if (rows.Count < 2)
        {
            return;
        }

        var seen = new Dictionary<string, int>(rows.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var deduplicationKey = row.DeduplicationKey;
            if (deduplicationKey is null)
            {
                continue;
            }

            var scope = row.ParentId is { } parentId
                ? $"child:{parentId.ToString(CultureInfo.InvariantCulture)}"
                : $"root:{row.NamespaceName}";
            var composite = scope + "\0" + deduplicationKey;

            if (seen.TryGetValue(composite, out var firstOrdinal))
            {
                throw row.ParentId is { } duplicateParentId
                    ? DuplicateDeduplicationKeyInBatchException.ForChild(duplicateParentId, deduplicationKey, firstOrdinal, i)
                    : DuplicateDeduplicationKeyInBatchException.ForRoot(row.NamespaceName, deduplicationKey, firstOrdinal, i);
            }

            seen.Add(composite, i);
        }
    }
}
