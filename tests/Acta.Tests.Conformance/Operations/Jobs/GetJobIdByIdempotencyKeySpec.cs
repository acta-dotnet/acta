using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for <c>GetJobIdByIdempotencyKey</c> - maps a <c>(namespace, idempotency_key)</c> pair to
/// the framework-assigned <c>job.id</c>. A known key resolves to the enqueued job's id; an unknown
/// key returns <c>null</c>.
/// </summary>
[ConformanceSpec(
    "get-job-id-by-idempotency-key.lookup",
    "GetJobIdByIdempotencyKey returns the id for a known key, null otherwise",
    Area = "Enqueue",
    Contract = "GetJobIdByIdempotencyKey resolves a root job's id from its namespace and idempotency key, and returns null when no row matches."
)]
[Deploys("add-numbers")]
public sealed class GetJobIdByIdempotencyKeySpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<GetJobIdByIdempotencyKeySpec>>
{
    [Fact(DisplayName = "Known idempotency key resolves to the enqueued job id and an unknown key returns null")]
    public async Task Known_idempotency_key_resolves_to_job_id_and_unknown_returns_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var idempotencyKey = Unique("ck-resolve");

        var outcome = await Jobs.EnqueueAsync(new AddNumbers(1, 2), o => o.IdempotencyKey(idempotencyKey), ct);
        Assert.Equal(EnqueueJobAction.Inserted, outcome.Action);

        var resolved = await Services
            .GetRequiredService<GetJobIdByIdempotencyKeyHandler>()
            .HandleAsync(new GetJobIdByIdempotencyKeyQuery(TestNamespace, idempotencyKey), ct);
        Assert.Equal(outcome.JobId, resolved?.JobId);

        var missing = await Services
            .GetRequiredService<GetJobIdByIdempotencyKeyHandler>()
            .HandleAsync(new GetJobIdByIdempotencyKeyQuery(TestNamespace, "no-such-key"), ct);
        Assert.Null(missing);
    }
}
