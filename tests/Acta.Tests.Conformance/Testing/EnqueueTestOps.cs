using Acta.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Test-support enqueue over the enqueue handlers' inserts with the production row pipeline: canonicalization,
/// per-row validation, same-batch deduplication-key uniqueness, C#-side ref allocation, and ordinal
/// reassembly - so specs that build raw rows exercise the same path the handlers run.
/// </summary>
internal static class EnqueueTestOps
{
    public static async Task<IReadOnlyList<JobEnqueueResponse>> EnqueueBatchAsync(
        IServiceProvider services,
        IReadOnlyList<JobEnqueueValues> rows,
        CancellationToken ct
    )
    {
        var canonical = new JobEnqueueValues[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            canonical[i] = JobEnqueue.Canonicalize(rows[i]);
            JobEnqueue.ValidateRow(canonical[i], i);
        }

        EnqueueBatchHandler.ValidateDeduplicationKeyUniqueness(canonical);

        var refs = new Guid[canonical.Length];
        for (var i = 0; i < refs.Length; i++)
        {
            refs[i] = JobRef.New().Value;
        }

        if (canonical.Length == 1)
        {
            var one = await services.GetRequiredService<EnqueueOneHandler>().InsertAsync(canonical[0], refs[0], null, ct);
            return [new JobEnqueueResponse(one[0].JobId, new JobRef(one[0].JobRef), one[0].Action)];
        }

        var outcomes = new JobEnqueueResponse[canonical.Length];
        foreach (var row in await services.GetRequiredService<EnqueueBatchHandler>().InsertAsync(canonical, refs, null, ct))
        {
            outcomes[row.Ordinal] = new JobEnqueueResponse(row.JobId, new JobRef(row.JobRef), row.Action);
        }

        return outcomes;
    }

    public static async Task<JobEnqueueResponse> EnqueueOneAsync(IServiceProvider services, JobEnqueueValues row, CancellationToken ct) =>
        (await EnqueueBatchAsync(services, [row], ct))[0];
}
