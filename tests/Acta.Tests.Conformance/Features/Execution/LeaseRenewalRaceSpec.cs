using System.Data.Common;
using Acta.Relational.Commands;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Execution;

/// <summary>
/// A child's completion locks its own runtime row and then its parent's, while the lease renewal walks
/// the worker's in-flight rows in job-id order, parent first. When one worker runs both, the two meet
/// in opposite orders. The renewal skips a row another transaction holds and reports it unrenewed, so
/// it never waits. Deadlock retry is off, so a deadlock fails the test instead of being retried.
/// SQLite runs one writer at a time, so the two cannot interleave there.
/// </summary>
[ConformanceSpec(
    "execution.lease-renewal-race",
    "A lease renewal never deadlocks with a child's completion",
    Area = "Execution",
    Contract = "A lease renewal skips an in-flight row another transaction holds, reporting it unrenewed, so it never waits on a completing child.",
    Arrange = "One worker runs a parent and its child, and the child's completion is held after it locked the child's row.",
    Act = "The worker renews its leases while the completion waits, and then the completion is released.",
    Assert = "Both succeed without a deadlock, the parent's lease is renewed, and the held child is reported still in flight."
)]
[CoversStoreMethod(typeof(IWorkerStore), nameof(IWorkerStore.ExtendWorkerLeasesAsync))]
public abstract class LeaseRenewalRaceSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        var provider = services.Last(d => d.ServiceType == typeof(SqlProviderOptions));
        services.Remove(provider);
        services.AddSingleton(sp =>
        {
            var options = (SqlProviderOptions)provider.ImplementationFactory!(sp);
            DeadlockRetryOff.Apply(sp, options);
            return options;
        });
    }

    [Fact(DisplayName = "A lease renewal racing a held child completion finishes without a deadlock")]
    public async Task Renewal_and_child_completion_do_not_deadlock()
    {
        var ct = TestContext.Current.CancellationToken;
        if (Db.Provider == DbProvider.Sqlite)
        {
            Assert.Skip("SQLite runs one writer at a time, so a renewal cannot interleave with a completion.");
        }

        var parent = await Jobs.EnqueueAsync(Step("p"), ct);
        var child = await Jobs.EnqueueAsync(Step("c") with { ParentJobId = parent.JobId }, ct);
        var (workerId, _) = await StartedAsync(parent, ct);
        var (_, complete) = await StartedAsync(child, ct);
        var latch = $"sys.child.{child.JobId}";
        await Db.ExecuteRawAsync(
            "INSERT INTO {schema}.checkpoints (job_id, kind_code, name, status_code) VALUES (@p_id, 50, @p_name, 10)",
            ct,
            ("@p_id", parent.JobId),
            ("@p_name", latch)
        );

        // Holding the parent's child latch stops the completion after it locked the child's row.
        await using var gateConnection = await Db.OpenConnectionAsync(ct);
        await using var gate = await gateConnection.BeginTransactionAsync(ct);
        await HoldLatchAsync(gateConnection, gate, parent.JobId, latch, ct);

        var completion = complete();
        await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
        Assert.False(completion.IsCompleted, "the completion did not wait for the parent's latch");
        var store = Services.GetRequiredService<IWorkerStore>();
        var leaseTtl = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;
        // The renewal must finish while the completion still holds the child's row; one that waited for it
        // would hold the parent's row and deadlock with the completion once the gate opened.
        var rows = await store.ExtendWorkerLeasesAsync(workerId, leaseTtl, draining: false, ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        await gate.CommitAsync(ct);

        Assert.Equal(CompleteExecutionAction.Completed, (await completion).Action);
        Assert.Contains(rows, r => r.JobId == parent.JobId && r.Renewed);
        // Held by the completion, the child is skipped; a slow completion that had not reached it yet lets
        // the renewal take it first. Either way it is still reported in flight.
        Assert.Contains(rows, r => r.JobId == child.JobId);
    }

    private async Task HoldLatchAsync(DbConnection conn, DbTransaction tx, long jobId, string name, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            $"UPDATE {Db.Schema}.checkpoints SET version = version + 1 WHERE job_id = @p_id AND kind_code = 50 AND name = @p_name";
        var id = cmd.CreateParameter();
        id.ParameterName = "@p_id";
        id.Value = jobId;
        cmd.Parameters.Add(id);
        var latch = cmd.CreateParameter();
        latch.ParameterName = "@p_name";
        latch.Value = name;
        cmd.Parameters.Add(latch);
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync(ct));
    }

    // Claims and starts the job as this namespace's worker and hands back its completion, unsent.
    private async Task<(int WorkerId, Func<Task<CompleteExecutionResult>> Complete)> StartedAsync(
        JobEnqueueOutcome job,
        CancellationToken ct
    )
    {
        var store = Services.GetRequiredService<IExecutionStore>();
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var leaseTtl = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;
        var worker = await Db.From<JobWorker>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        var workerId = Assert.IsType<JobWorker>(worker).Id;
        var claimed = Assert.Single(await store.ClaimOneAsync(ns, workerId, leaseTtl, job.JobId, ct));
        Assert.Equal(
            StartExecutionAction.Started,
            await store.StartExecutionAsync(claimed.JobId, workerId, claimed.ExecutionNumber, claimed.Version, leaseTtl, ct)
        );
        return (
            workerId,
            () =>
                store.CompleteExecutionAsync(
                    new CompleteExecutionRequest(
                        JobId: claimed.JobId,
                        WorkerId: workerId,
                        ExpectedExecutionNumber: claimed.ExecutionNumber,
                        Outcome: ExecutionOutcome.Succeeded,
                        ResultFormatId: 0,
                        Result: ReadOnlyMemory<byte>.Empty
                    ),
                    ct
                )
        );
    }

    private JobEnqueueRequest Step(string label) => new(TestNamespace, "lane-step", JobPayload.Json(new LaneStep("", label)));
}
