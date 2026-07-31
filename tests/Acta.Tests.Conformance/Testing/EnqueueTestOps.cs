using Acta.Runtime.Modules.Execution.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Test-support enqueue over the job store port with the production row pipeline: canonicalization,
/// per-row validation, same-batch deduplication-key uniqueness, C#-side ref allocation, and ordinal
/// reassembly - so specs that build raw rows exercise the same path the service runs.
/// </summary>
internal static class EnqueueTestOps
{
    public static async Task<IReadOnlyList<JobEnqueueOutcome>> EnqueueBatchAsync(
        IServiceProvider services,
        IReadOnlyList<JobEnqueueRow> rows,
        CancellationToken ct
    )
    {
        var store = services.GetRequiredService<IJobStore>();
        var canonical = new JobEnqueueRow[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            canonical[i] = JobEnqueueRows.Canonicalize(rows[i]);
            JobEnqueueRows.ValidateRow(canonical[i], i);
        }

        JobsService.ValidateDeduplicationKeyUniqueness(canonical);

        var refs = new Guid[canonical.Length];
        for (var i = 0; i < refs.Length; i++)
        {
            refs[i] = JobRef.New().Value;
        }

        var outcomeRows =
            canonical.Length == 1
                ? await store.EnqueueOneAsync(canonical[0], refs[0], ct)
                : await store.EnqueueBatchAsync(canonical, refs, ct);

        var outcomes = new JobEnqueueOutcome[canonical.Length];
        foreach (var row in outcomeRows)
        {
            outcomes[row.Ordinal] = new JobEnqueueOutcome(row.JobId, new JobRef(row.JobRef), row.Action);
        }

        return outcomes;
    }

    public static async Task<JobEnqueueOutcome> EnqueueOneAsync(IServiceProvider services, JobEnqueueRow row, CancellationToken ct) =>
        (await EnqueueBatchAsync(services, [row], ct))[0];
}
