using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Retiring a definition cancels its parked laned jobs in the same transaction as the retire, and
/// hands each lane to its next member only after every one of them is cancelled, so no member of the
/// retired definition is promoted and nothing depends on the caller staying around after the commit.
/// </summary>
[ConformanceSpec(
    "lanes.retire",
    "A retire cancels its laned jobs atomically and promotes each lane once",
    Area = "Lanes",
    Contract = "A definition retire cancels its parked laned jobs in its own transaction and then promotes each lane's lowest Blocked member once, even if the caller is gone.",
    Arrange = "A lane holds two parked jobs of one definition ahead of a job of another definition, beside an unlaned job of the first.",
    Act = "The first definition is retired by a caller whose token is cancelled at the first wake it publishes.",
    Assert = "No job of the retired definition is live, and the other definition's job leads the lane Ready."
)]
public abstract class LaneRetireSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private readonly CancelOnFirstWake _wakeups = new();

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        // Registered ahead of UseActa's TryAddSingleton, so the retire publishes through it.
        services.AddSingleton<IWorkerWakeup>(_wakeups);
        base.ConfigureServices(services, testNamespace);
    }

    [Fact(DisplayName = "A retire whose caller is cancelled after the commit still leaves no live laned job of the definition")]
    public async Task Retire_cancels_laned_jobs_in_its_own_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var loose = await Jobs.EnqueueAsync(Step("", "loose", lane: null), ct);
        var head = await Jobs.EnqueueAsync(Step("orders", "head", "orders"), ct);
        var follower = await Jobs.EnqueueAsync(Step("orders", "follower", "orders"), ct);
        var other = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "lane-defined", JobPayload.Json(new LaneDefinedStep("other")), Lane: "orders"),
            ct
        );
        var definition = await Operations.Definitions.GetAsync(TestNamespace, "lane-step", ct);

        using var caller = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _wakeups.Cancel = caller;
        try
        {
            await Operations.Definitions.RetireAsync(TestNamespace, "lane-step", definition!.Version, "tester", "retired", caller.Token);
        }
        catch (OperationCanceledException)
        {
            // The caller went away after the retire committed; what it left behind is the point.
        }

        foreach (var job in new[] { loose, head, follower })
        {
            Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(job.JobId, ct)).Status);
        }

        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(other.JobId, ct)).Status);
    }

    private JobEnqueueRequest Step(string probeLane, string label, string? lane) =>
        new(TestNamespace, "lane-step", JobPayload.Json(new LaneStep(probeLane, label)), Lane: lane);

    /// <summary>The in-process wakeup, cancelling the caller's token at the first publish it sees.</summary>
    private sealed class CancelOnFirstWake : IWorkerWakeup
    {
        private readonly InProcessWakeup _inner = new();

        public CancellationTokenSource? Cancel { get; set; }

        public ValueTask WakeAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, CancellationToken ct = default)
        {
            Cancel?.Cancel();
            return _inner.WakeAsync(channel, reason, ct);
        }

        public ValueTask<WorkerWakeupWaitStatus> WaitAsync(WorkerWakeupChannel channel, TimeSpan timeout, CancellationToken ct) =>
            _inner.WaitAsync(channel, timeout, ct);
    }
}
