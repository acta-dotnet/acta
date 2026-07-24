using System.Transactions;
using Acta.Payloads;
using Acta.Tests.Conformance.Sqlite.Testing;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Sqlite.DbSession;

/// <summary>
/// The ambient-scope guard is provider-neutral code on the single owned connection-open seam
/// (<c>DbSession.OpenConnectionAsync</c>), so it is proven once on the SQLite head rather than across a
/// per-provider matrix: an owned enqueue inside an active <see cref="TransactionScope"/> fails fast with
/// the documented rejection naming the correct rewrites, and the same enqueue inside
/// <c>TransactionScope(Suppress)</c> succeeds with owned semantics.
/// </summary>
public sealed class SqliteAmbientTransactionScopeSpec : ActaRuntimeTestBase<SqliteConformanceFixture, TestJobsManifest>
{
    private async Task<JobEnqueueOutcome> EnqueueAsync(CancellationToken ct)
    {
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(2, 3));
        return await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", payload, DeduplicationKey: TestKey("ambient")),
            ct
        );
    }

    [Fact(DisplayName = "An owned enqueue inside an ambient TransactionScope throws the documented rejection")]
    public async Task Owned_enqueue_inside_an_ambient_scope_throws_with_rewrite_guidance()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => EnqueueAsync(ct));

        Assert.Contains("TransactionScope", ex.Message);
        Assert.Contains("Suppress", ex.Message);
        Assert.Contains("AddToActaOutboxAsync", ex.Message);
        // The scope is left uncompleted: nothing enlisted, so its dispose is a no-op rollback.
    }

    [Fact(DisplayName = "An owned enqueue inside TransactionScope(Suppress) succeeds with owned semantics")]
    public async Task Owned_enqueue_inside_a_suppress_scope_succeeds()
    {
        var ct = TestContext.Current.CancellationToken;

        JobEnqueueOutcome outcome;
        using (var scope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
        {
            outcome = await EnqueueAsync(ct);
            scope.Complete();
        }

        Assert.True(outcome.JobId > 0);
        Assert.Equal(JobEnqueueAction.Inserted, outcome.Action);
    }
}
