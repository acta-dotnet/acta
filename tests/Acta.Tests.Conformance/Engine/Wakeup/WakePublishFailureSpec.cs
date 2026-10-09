using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Wakeup;

/// <summary>
/// A wake is published after the write it announces has committed, so a transport that throws costs a waiter its prompt
/// wake and nothing more: the enqueue still answers, and the job it wrote is claimed and runs to completion.
/// </summary>
[ConformanceSpec(
    "wakeup.publish-failure",
    "A failed wake publish never fails the write it announces",
    Area = "Wakeup",
    Contract = "A wake transport that throws costs only the prompt wake: the enqueue still answers, and its job runs to completion."
)]
public sealed class WakePublishFailureSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobsManifest>
{
    private readonly ThrowingWakeup _wakeup = new();

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        // Registered ahead of UseActa's TryAddSingleton, so every publish in this host goes through the throwing transport.
        services.AddSingleton<IWorkerWakeup>(_wakeup);
        base.ConfigureServices(services, testNamespace);
    }

    [Fact(DisplayName = "An enqueue whose wake publish throws still answers, and its job runs to completion")]
    public async Task Throwing_transport_leaves_the_enqueue_and_its_run_intact()
    {
        var ct = TestContext.Current.CancellationToken;

        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );

        Assert.True(_wakeup.Failures > 0);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));
    }

    private sealed class ThrowingWakeup : IWorkerWakeup
    {
        private readonly InProcessWakeup _inner = new();
        private int _failures;

        public int Failures => Volatile.Read(ref _failures);

        public ValueTask WakeAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _failures);
            throw new InvalidOperationException("The wake transport is unreachable.");
        }

        public ValueTask<WorkerWakeupWaitStatus> WaitAsync(WorkerWakeupChannel channel, TimeSpan timeout, CancellationToken ct) =>
            _inner.WaitAsync(channel, timeout, ct);
    }
}
