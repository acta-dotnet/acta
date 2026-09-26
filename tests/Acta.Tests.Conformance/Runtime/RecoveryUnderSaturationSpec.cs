using Acta.Relational.Entities;
using Acta.Runtime.Hosting;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// The recovery sweep on a worker whose executors are all held. The Buffered claim loop claims ahead
/// of its executors into a channel, so the <c>sys.recovery</c> slot could be claimed into that buffer
/// behind jobs no executor will take, under a lease the heartbeat renews, and nothing would sweep
/// until an executor came free. The loop runs the slot on its own task instead, so the one job that
/// reclaims what the executors hold never waits behind them.
/// </summary>
[ConformanceSpec(
    "runtime.recovery-under-saturation",
    "A Buffered worker with every executor held still runs the recovery sweep",
    Area = "Recovery",
    Contract = "A recovery slot claimed by the Buffered loop runs outside the channel, so a stranded job is reclaimed while every executor is held.",
    Arrange = "A Buffered worker with one executor held by a blocking job, a job stranded under a lapsed lease, and the recovery slot due.",
    Act = "The claim loop takes the due slot while the executor is still held.",
    Assert = "The stranded job returns to Ready before the executor is released."
)]
[CoversStoreMethod(
    typeof(Acta.Runtime.Modules.Execution.IExecutionStore),
    nameof(Acta.Runtime.Modules.Execution.IExecutionStore.ClaimBatchAsync)
)]
public abstract class RecoveryUnderSaturationSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // The recovery slot is a system slot; the base leaves system slots out unless a spec asks for them.
    protected override bool RegisterSystemJobs => true;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.Configure<JobsOptions>(o =>
        {
            o.ExecutionProfile = ExecutionProfile.Buffered;
            o.MaxConcurrentExecutors = 1;
            o.SafetyPollInterval = TimeSpan.FromSeconds(1);
            // A long beat keeps the heartbeat's own orphan release out of the window, so the stranded job
            // can only return to Ready through the sweep this fact is about.
            o.HeartbeatInterval = TimeSpan.FromSeconds(30);
            o.LeaseTtlSeconds = 120;
            o.WorkerDeadAfter = TimeSpan.FromSeconds(300);
        });
    }

    [Fact(DisplayName = "A stranded job is reclaimed while the Buffered worker's only executor is held by another job")]
    public async Task Sweep_runs_while_the_only_executor_is_held()
    {
        var ct = TestContext.Current.CancellationToken;
        var blocking = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "chaos-holding", JobPayload.None), ct);
        ChaosProbes.Reset(blocking.JobId);

        using var hostCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = Runtime.RunAsync(hostCts.Token);
        try
        {
            // The only executor is now held for as long as this fact wants.
            await ChaosProbes.WaitStartedAsync(blocking.JobId, ct).WaitAsync(SpecWaits.Gate, ct);

            // A job stranded the way a dead worker leaves one: in flight under a lease that lapsed. The
            // slot is made due so the next claim can take it.
            var stranded = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct);
            await StrandAsync(stranded.JobId, ct);
            var slotId = await Jobs.GetJobIdAsync(JobLookup.ByDeduplicationKey(TestNamespace, "sys.recovery"), ct);
            Assert.NotNull(slotId);
            await ChaosSpecHelpers.SetReadyAsync(Db, slotId!.Value, ct);

            // A fresh enqueue wakes the claim loop; it claims the slot alongside and must run the sweep
            // even though nothing can reach an executor.
            await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct);

            // The sweep's reclaim is read from the ledger rather than from the row: a reclaimed row is Ready
            // for only as long as the claim loop takes to buffer it again; the holding job keeps its
            // executor for the whole fact, and the ledger is the proof the sweep ran meanwhile.
            var cancelled = ChaosProbes.WaitCancelledAsync(blocking.JobId, ct);
            var deadline = DateTime.UtcNow + SpecWaits.Converge;
            var reclaimed = false;
            do
            {
                var events = await Db.From<JobEvent>().Where(e => e.JobId == stranded.JobId).ToListAsync(ct);
                if (events.Any(e => e.ReasonCode == JobEventReasonCode.JobLeaseExpired))
                {
                    reclaimed = true;
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            } while (DateTime.UtcNow < deadline);

            Assert.True(reclaimed, "the stranded job was not reclaimed within the converge budget");
            Assert.False(cancelled.IsCompleted, "the executor was already free when the sweep ran, so the fact proved nothing");
            Assert.Equal(1, ChaosProbes.CountingInvocations[blocking.JobId]);
        }
        finally
        {
            ChaosProbes.Release(blocking.JobId);
            await hostCts.CancelAsync();
            try
            {
                await run.WaitAsync(SpecWaits.Gate, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // The host token ended the run.
            }
        }
    }

    // In flight under this namespace's worker with a lease five minutes gone, as a dead worker leaves a row.
    private async Task StrandAsync(long jobId, CancellationToken ct)
    {
        var namespaceId = await ChaosSpecHelpers.NamespaceIdAsync(Db, TestNamespace, ct);
        var workerId = await ChaosSpecHelpers.WorkerIdAsync(Db, namespaceId, ct);
        await Db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET status_code = @p_status, execution_number = execution_number + 1, "
                + "leased_by_worker_id = @p_worker, lease_expires_at_utc = @p_expires WHERE job_id = @p_id",
            ct,
            ("@p_status", (byte)JobStatusCode.Executing),
            ("@p_worker", workerId),
            ("@p_expires", DateTime.UtcNow.AddMinutes(-5)),
            ("@p_id", jobId)
        );
    }
}
