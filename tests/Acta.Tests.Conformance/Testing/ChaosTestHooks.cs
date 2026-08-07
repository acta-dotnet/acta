using Acta.Runtime.Kernel;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Checkpoints;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Runtime.Services.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Acta.Tests.Conformance.Testing;

internal sealed class StoreFaultPlan
{
    private int _throwBeforeComplete;
    private int _throwAfterComplete;
    private TimeSpan? _getUtcNowSkew;

    public void ThrowBeforeCompleteOnce() => Interlocked.Exchange(ref _throwBeforeComplete, 1);

    public void ThrowAfterCompleteOnce() => Interlocked.Exchange(ref _throwAfterComplete, 1);

    public void SkewGetUtcNowBy(TimeSpan skew) => _getUtcNowSkew = skew;

    public void MaybeThrowBefore(string operation)
    {
        if (operation == "CompleteExecution" && Interlocked.Exchange(ref _throwBeforeComplete, 0) == 1)
        {
            throw new TimeoutException("Injected transient failure before CompleteExecution.");
        }
    }

    public void MaybeThrowAfter(string operation)
    {
        if (operation == "CompleteExecution" && Interlocked.Exchange(ref _throwAfterComplete, 0) == 1)
        {
            throw new TimeoutException("Injected transient failure after CompleteExecution.");
        }
    }

    public bool TryReadSkewedGetUtcNow(out DateTime value)
    {
        if (_getUtcNowSkew is { } skew)
        {
            value = DateTime.UtcNow + skew;
            return true;
        }

        value = default;
        return false;
    }
}

// The completion write moved off IDbSession onto IExecutionStore (its own connection), so the
// before/after-CompleteExecution chaos injection has to wrap that port. Only CompleteExecutionAsync
// carries the hooks; every other method delegates straight through.
internal sealed class FaultInjectingExecutionStore(IExecutionStore inner, StoreFaultPlan plan) : IExecutionStore
{
    public async Task<CompleteExecutionResult> CompleteExecutionAsync(CompleteExecutionRequest request, CancellationToken ct)
    {
        plan.MaybeThrowBefore("CompleteExecution");
        var result = await inner.CompleteExecutionAsync(request, ct);
        plan.MaybeThrowAfter("CompleteExecution");
        return result;
    }

    public Task<CheckpointSlotRow> CheckpointSlotAsync(CheckpointSlotCommand command, CancellationToken ct) =>
        inner.CheckpointSlotAsync(command, ct);

    public Task<IReadOnlyList<long>> GetChildJobIdsAsync(long parentJobId, CancellationToken ct) =>
        inner.GetChildJobIdsAsync(parentJobId, ct);

    public Task<IReadOnlyList<Acta.Runtime.Modules.Execution.ChildLatches.StaleChildLatch>> GetStaleChildLatchesAsync(
        short namespaceId,
        CancellationToken ct
    ) => inner.GetStaleChildLatchesAsync(namespaceId, ct);

    public Task<Acta.Runtime.Modules.Execution.Timers.SleepDecision> ArmOrConsumeSleepTimerAsync(
        ArmOrConsumeSleepTimerCommand command,
        CancellationToken ct
    ) => inner.ArmOrConsumeSleepTimerAsync(command, ct);

    public Task<ClaimResult> ClaimBatchAsync(ClaimRequest request, int leaseTtlSeconds, CancellationToken ct) =>
        inner.ClaimBatchAsync(request, leaseTtlSeconds, ct);

    public Task<ClaimResult> ClaimOneAsync(ClaimRequest request, int leaseTtlSeconds, long? jobId, CancellationToken ct) =>
        inner.ClaimOneAsync(request, leaseTtlSeconds, jobId, ct);

    public Task<StartExecutionAction> StartExecutionAsync(
        long jobId,
        int workerId,
        int expectedExecutionNumber,
        int expectedVersion,
        int leaseTtlSeconds,
        CancellationToken ct
    ) => inner.StartExecutionAsync(jobId, workerId, expectedExecutionNumber, expectedVersion, leaseTtlSeconds, ct);

    public Task<IReadOnlyList<bool>> CompleteExecutionsBatchAsync(IReadOnlyList<CompleteExecutionRequest> requests, CancellationToken ct) =>
        inner.CompleteExecutionsBatchAsync(requests, ct);

    public Task<ReclaimStuckJobsResult> ReclaimStuckJobsAsync(short namespaceId, CancellationToken ct) =>
        inner.ReclaimStuckJobsAsync(namespaceId, ct);

    public Task<StartStepDecision> StartStepAsync(long jobId, string name, bool atMostOnce, CancellationToken ct) =>
        inner.StartStepAsync(jobId, name, atMostOnce, ct);

    public Task<CompleteStepDecision> CompleteStepAsync(CompleteStepCommand command, CancellationToken ct) =>
        inner.CompleteStepAsync(command, ct);
}

// The DB clock moved off IDbSession onto IActaClock, so the GetUtcNow skew now wraps that port.
internal sealed class FaultInjectingClock(IServerClock inner, StoreFaultPlan plan) : IServerClock
{
    public ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct) =>
        plan.TryReadSkewedGetUtcNow(out var skewed) ? new ValueTask<DateTime>(skewed) : inner.GetUtcNowAsync(ct);
}

internal static class ChaosServiceCollectionExtensions
{
    public static StoreFaultPlan AddStoreFaultInjection(this IServiceCollection services)
    {
        var plan = new StoreFaultPlan();
        services.AddSingleton(plan);

        var executionDescriptor = services.Last(d => d.ServiceType == typeof(IExecutionStore));
        services.Remove(executionDescriptor);
        services.AddSingleton<IExecutionStore>(sp => new FaultInjectingExecutionStore(
            (IExecutionStore)CreateInner(sp, executionDescriptor),
            sp.GetRequiredService<StoreFaultPlan>()
        ));

        var clockDescriptor = services.Last(d => d.ServiceType == typeof(IServerClock));
        services.Remove(clockDescriptor);
        services.AddSingleton<IServerClock>(sp => new FaultInjectingClock(
            (IServerClock)CreateInner(sp, clockDescriptor),
            sp.GetRequiredService<StoreFaultPlan>()
        ));

        return plan;
    }

    private static object CreateInner(IServiceProvider sp, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is { } instance)
        {
            return instance;
        }

        return descriptor.ImplementationFactory is { } factory ? factory(sp)!
            : descriptor.ImplementationType is { } type ? ActivatorUtilities.CreateInstance(sp, type)
            : throw new InvalidOperationException("Unsupported service descriptor.");
    }

    public static ControlledWakeup AddControlledWakeup(this IServiceCollection services)
    {
        var wakeup = new ControlledWakeup();
        services.Replace(ServiceDescriptor.Singleton<IWorkerWakeup>(wakeup));
        services.Replace(
            ServiceDescriptor.Singleton(sp => new WorkerWakeupPublisher(
                sp.GetRequiredService<IWorkerWakeup>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<WorkerWakeupPublisher>>(),
                sp.GetService<JobMetrics>()
            ))
        );
        return wakeup;
    }
}

internal sealed class ControlledWakeup : IWorkerWakeup
{
    private readonly Lock _gate = new();
    private readonly List<(WorkerWakeupChannelKind Kind, int Threshold, TaskCompletionSource Tcs)> _wakeWaiters = [];
    private readonly Dictionary<WorkerWakeupChannelKind, int> _wakesByKind = [];
    private TaskCompletionSource? _waiting;
    private WorkerWakeupWaitResult _nextResult = WorkerWakeupWaitResult.TimedOut;

    public int WakeCount { get; private set; }

    public Task WaiterReady => WaiterReadySource.Task;

    private TaskCompletionSource WaiterReadySource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask WakeAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, CancellationToken ct = default)
    {
        List<TaskCompletionSource>? matured = null;
        lock (_gate)
        {
            WakeCount++;
            var kindCount = _wakesByKind.GetValueOrDefault(channel.Kind) + 1;
            _wakesByKind[channel.Kind] = kindCount;
            for (var i = _wakeWaiters.Count - 1; i >= 0; i--)
            {
                if (_wakeWaiters[i].Kind == channel.Kind && kindCount >= _wakeWaiters[i].Threshold)
                {
                    (matured ??= []).Add(_wakeWaiters[i].Tcs);
                    _wakeWaiters.RemoveAt(i);
                }
            }
        }

        matured?.ForEach(tcs => tcs.TrySetResult());
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Completes when at least <paramref name="threshold"/> wakes have been published on channels of
    /// <paramref name="kind"/>. This is the outcome-not-clock instrument: a JobCompletion wake is
    /// published only after the terminal completion write returns, so awaiting it proves the job ran
    /// to completion without any wall-clock budget.
    /// </summary>
    public Task WaitForWakeAsync(WorkerWakeupChannelKind kind, int threshold, CancellationToken ct)
    {
        TaskCompletionSource tcs;
        lock (_gate)
        {
            if (_wakesByKind.GetValueOrDefault(kind) >= threshold)
            {
                return Task.CompletedTask;
            }

            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _wakeWaiters.Add((kind, threshold, tcs));
        }

        return tcs.Task.WaitAsync(ct);
    }

    public async ValueTask<WorkerWakeupWaitResult> WaitAsync(WorkerWakeupChannel channel, TimeSpan timeout, CancellationToken ct)
    {
        TaskCompletionSource waiter;
        lock (_gate)
        {
            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting = waiter;
            WaiterReadySource.TrySetResult();
        }

        await waiter.Task.WaitAsync(ct);
        return _nextResult;
    }

    public void ReleaseWait(WorkerWakeupWaitResult result = WorkerWakeupWaitResult.TimedOut)
    {
        TaskCompletionSource? waiter;
        lock (_gate)
        {
            _nextResult = result;
            waiter = _waiting;
            _waiting = null;
        }

        waiter?.TrySetResult();
    }
}
