using System.Collections.Concurrent;
using Acta;

namespace TestJobs;

/// <summary>
/// A handler that acquires a <see cref="JobContext.RunWithLockAsync(string, Func{Task}, TimeSpan?, LockScope, CancellationToken)"/>
/// lock and holds it inside the critical section until the test releases it (happy path) or the
/// attempt token is cancelled (lost-lock path). Per-namespace signals keep parallel runs isolated.
/// Used to prove the worker heartbeat extends handler-acquired locks and cancels the attempt when one
/// is stolen mid-section.
/// </summary>
public static class LockHolder
{
    /// <summary>The unscoped lock name; the composed key is <c>{namespaceId}.lock.hold</c>.</summary>
    public const string LockName = "hold";

    private static readonly ConcurrentDictionary<string, TaskCompletionSource> _entered = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, TaskCompletionSource> _release = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _observed = new(StringComparer.Ordinal);

    public static void Reset(string jobNamespace)
    {
        _entered[jobNamespace] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _release[jobNamespace] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _observed[jobNamespace] = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Completes once the handler is inside the lock's critical section.</summary>
    public static Task Entered(string jobNamespace) => _entered[jobNamespace].Task;

    /// <summary>Completes with <c>true</c> once the handler observed cancellation while holding the lock.</summary>
    public static Task<bool> Observed(string jobNamespace) => _observed[jobNamespace].Task;

    /// <summary>Let the handler exit the critical section and release the lock normally.</summary>
    public static void Release(string jobNamespace) => _release[jobNamespace].TrySetResult();

    [Job("lock-holder")]
    public static Task Run(JobContext ctx, CancellationToken ct) =>
        ctx.RunWithLockAsync(
            LockName,
            async () =>
            {
                _entered[ctx.JobNamespace].SetResult();
                try
                {
                    await _release[ctx.JobNamespace].Task.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    _observed[ctx.JobNamespace].SetResult(true);
                    throw;
                }
            },
            ct: ct
        );
}
