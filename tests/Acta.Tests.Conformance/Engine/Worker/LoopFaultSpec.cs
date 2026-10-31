using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// A worker loop that faults ends the worker: its fault stops the other loops and comes out of
/// <see cref="WorkerRuntime.RunAsync"/>, so the host sees it, instead of a heartbeat that keeps the worker Active while
/// nothing claims. The fault here is the claim loop's idle wait, which a wake transport can break.
/// </summary>
[ConformanceSpec(
    "worker.loop-fault",
    "A worker loop that faults ends the worker with its fault",
    Area = "Recovery",
    Contract = "A loop of a running worker that faults stops its other loops, and RunAsync ends with that fault instead of heartbeating with no claim loop."
)]
public sealed class LoopFaultSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobsManifest>
{
    private readonly BreakingWakeup _wakeup = new();

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        // Registered ahead of UseActa's TryAddSingleton, so the claim loop waits through it.
        services.AddSingleton<IWorkerWakeup>(_wakeup);
        base.ConfigureServices(services, testNamespace);
    }

    [Fact(DisplayName = "A claim loop whose idle wait throws ends RunAsync with that fault and stops the heartbeat with it")]
    public async Task A_faulted_claim_loop_ends_the_worker()
    {
        var ct = TestContext.Current.CancellationToken;
        _wakeup.Broken = true;
        using var hostCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var run = Runtime.RunAsync(hostCts.Token);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(SpecWaits.Gate, ct));
        Assert.Equal("The wake transport broke.", thrown.Message);
        Assert.False(hostCts.IsCancellationRequested);
    }

    private sealed class BreakingWakeup : IWorkerWakeup
    {
        private readonly InProcessWakeup _inner = new();

        public volatile bool Broken;

        public ValueTask WakeAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, CancellationToken ct = default) =>
            _inner.WakeAsync(channel, reason, ct);

        public ValueTask<WorkerWakeupWaitStatus> WaitAsync(WorkerWakeupChannel channel, TimeSpan timeout, CancellationToken ct) =>
            Broken && channel.Kind == WorkerWakeupChannelKind.WorkerNamespace
                ? throw new InvalidOperationException("The wake transport broke.")
                : _inner.WaitAsync(channel, timeout, ct);
    }
}
