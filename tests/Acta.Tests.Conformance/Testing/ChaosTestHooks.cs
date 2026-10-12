using System.Collections;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Acta.Tests.Conformance.Testing;

internal sealed class InjectedProviderError(string where = "before CompleteExecutions")
    : System.Data.Common.DbException($"Injected provider error {where}.");

internal sealed class StoreFaultPlan
{
    private int _throwBeforeComplete;
    private Func<Task>? _beforeComplete;
    private int _throwAfterComplete;
    private TimeSpan? _getUtcNowSkew;

    public void ThrowBeforeCompleteOnce() => Interlocked.Exchange(ref _throwBeforeComplete, 1);

    // A provider-shaped fault, the kind the completion write repeats; the TimeoutException above is
    // deliberately not one, so the crash-shaped facts keep staging an attempt that never lands.
    public void ThrowProviderErrorBeforeCompleteOnce() => Interlocked.Exchange(ref _throwBeforeComplete, 2);

    public void ThrowAfterCompleteOnce() => Interlocked.Exchange(ref _throwAfterComplete, 1);

    /// <summary>
    /// Turns back every completion until cleared, and counts the refusals. The write is repeated until it
    /// lands or the worker stops, so a fact about a completion that does not land has to keep refusing it:
    /// a single throw is now just a blip the repeat rides out.
    /// </summary>
    public void ThrowBeforeCompleteUntilCleared() => Interlocked.Exchange(ref _throwBeforeCompleteUntilCleared, 1);

    public void StopThrowingBeforeComplete() => Interlocked.Exchange(ref _throwBeforeCompleteUntilCleared, 0);

    public int CompletionRefusals => Volatile.Read(ref _completionRefusals);

    /// <summary>
    /// Narrows every completion fault to one job. A flush settles its entries side by side, so a fault
    /// that matched by call order would land on whichever entry reached the store first and a fact about
    /// one stranded job could name a different one on the next run.
    /// </summary>
    public void FailOnlyJob(long jobId) => Interlocked.Exchange(ref _failOnlyJob, jobId);

    private int _throwBeforeCompleteUntilCleared;
    private int _completionRefusals;
    private long _failOnlyJob;

    public void SkewGetUtcNowBy(TimeSpan skew) => _getUtcNowSkew = skew;

    private Func<Task>? _beforeStart;
    private int _throwAfterStart;
    private int _throwGetUtcNow;

    /// <summary>Runs <paramref name="action"/> once, between an attempt's claim and its start write.</summary>
    public void RunBeforeStartOnce(Func<Task> action) => Interlocked.Exchange(ref _beforeStart, action);

    /// <summary>Lets the next start write commit and then loses its answer, the way a dropped connection would.</summary>
    public void ThrowAfterStartOnce() => Interlocked.Exchange(ref _throwAfterStart, 1);

    private int _throwAfterClaim;

    /// <summary>Lets the next claim commit and then loses its answer: the rows are leased and nothing holds them.</summary>
    public void ThrowAfterClaimOnce() => Interlocked.Exchange(ref _throwAfterClaim, 1);

    public void MaybeThrowAfterClaim()
    {
        if (Interlocked.Exchange(ref _throwAfterClaim, 0) == 1)
        {
            throw new InjectedProviderError("after the claim");
        }
    }

    /// <summary>Fails the next database clock read with a provider error.</summary>
    public void ThrowGetUtcNowOnce() => Interlocked.Exchange(ref _throwGetUtcNow, 1);

    public Task RunBeforeStartAsync() => Interlocked.Exchange(ref _beforeStart, null) is { } action ? action() : Task.CompletedTask;

    public void MaybeThrowAfterStart()
    {
        if (Interlocked.Exchange(ref _throwAfterStart, 0) == 1)
        {
            throw new InjectedProviderError("after StartExecution");
        }
    }

    public bool TakeGetUtcNowFault() => Interlocked.Exchange(ref _throwGetUtcNow, 0) == 1;

    /// <summary>
    /// Runs <paramref name="action"/> once, inside the window between an attempt deciding its outcome
    /// and that outcome reaching the ledger.
    /// </summary>
    /// <remarks>
    /// This is how a race against an in-flight attempt is tested without racing anything: the window
    /// is normally microseconds wide, so a test that tried to hit it with a second thread would prove
    /// only which thread happened to win on that run. Suspending the completion instead makes the
    /// interleaving the test's choice, and the result reproducible on every provider.
    /// </remarks>
    public void RunBeforeCompleteOnce(Func<Task> action) => Interlocked.Exchange(ref _beforeComplete, action);

    public void MaybeThrowBefore(IReadOnlyList<long> jobIds)
    {
        var only = Interlocked.Read(ref _failOnlyJob);
        if (only != 0 && !jobIds.Contains(only))
        {
            return;
        }

        if (Interlocked.Exchange(ref _beforeComplete, null) is { } action)
        {
            action().GetAwaiter().GetResult();
        }

        if (Volatile.Read(ref _throwBeforeCompleteUntilCleared) == 1)
        {
            Interlocked.Increment(ref _completionRefusals);
            throw new InjectedProviderError();
        }

        switch (Interlocked.Exchange(ref _throwBeforeComplete, 0))
        {
            case 1:
                throw new TimeoutException("Injected transient failure before CompleteExecutions.");
            case 2:
                throw new InjectedProviderError();
        }
    }

    public void MaybeThrowAfter()
    {
        if (Interlocked.Exchange(ref _throwAfterComplete, 0) == 1)
        {
            throw new TimeoutException("Injected transient failure after CompleteExecutions.");
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

/// <summary>
/// The real session with the chaos hooks on the execution calls they stage: before and after the completion
/// write, after a claim, and around the start write. Every other call passes straight through.
/// </summary>
internal sealed class FaultInjectingSession(IDbSession inner, ISqlDialect dialect, StoreFaultPlan plan) : IDbSession
{
    public DbProvider Provider => inner.Provider;

    public string Schema => inner.Schema;

    public Task<DbConnection> OpenConnectionAsync(CancellationToken ct) => inner.OpenConnectionAsync(ct);

    public Task<T> QueryAsync<T>(
        string stem,
        Action<DbCommand> bind,
        Func<DbDataReader, CancellationToken, Task<T>> read,
        CancellationToken ct
    ) => inner.QueryAsync(stem, bind, read, ct);

    public Task<T> QueryAsync<T>(
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, CancellationToken, Task<T>> read,
        CancellationToken ct
    ) => inner.QueryAsync(command, bind, read, ct);

    public async Task<IReadOnlyList<T>> ExecuteAsync<T>(
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, T> mapRow,
        CancellationToken ct
    )
    {
        if (command.Operation == "StartExecution")
        {
            await plan.RunBeforeStartAsync();
        }

        if (command.Operation == "CompleteExecutions")
        {
            plan.MaybeThrowBefore(BoundJobIds(bind));
        }

        var rows = await inner.ExecuteAsync(command, bind, mapRow, ct);
        switch (command.Operation)
        {
            case "CompleteExecutions":
                plan.MaybeThrowAfter();
                break;
            case "ClaimJobs" or "ClaimJob":
                plan.MaybeThrowAfterClaim();
                break;
            case "StartExecution":
                plan.MaybeThrowAfterStart();
                break;
        }

        return rows;
    }

    public Task<IReadOnlyList<T>> ExecuteInTransactionAsync<T>(
        DbTransaction transaction,
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, T> mapRow,
        CancellationToken ct
    ) => inner.ExecuteInTransactionAsync(transaction, command, bind, mapRow, ct);

    public Task<T?> ExecuteSingleAsync<T>(StoreCommand command, Action<DbCommand> bind, Func<DbDataReader, T> mapRow, CancellationToken ct)
        where T : class => inner.ExecuteSingleAsync(command, bind, mapRow, ct);

    public Task ExecuteAsync(StoreCommand command, Action<DbCommand> bind, CancellationToken ct) => inner.ExecuteAsync(command, bind, ct);

    public Task<T> RunWithRetryAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct) =>
        inner.RunWithRetryAsync(action, ct);

    public Task<T> RunInOwnedTransactionAsync<T>(
        Func<DbTransaction, CancellationToken, Task<(T Result, bool Commit)>> work,
        CancellationToken ct
    ) => inner.RunInOwnedTransactionAsync(work, ct);

    // The jobs a completion batch names, read by binding it onto a command the provider never opens or runs: SQL
    // Server streams table-valued records, PostgreSQL binds an array, and SQLite binds JSON text.
    private IReadOnlyList<long> BoundJobIds(Action<DbCommand> bind)
    {
        using var connection = dialect.CreateConnection(string.Empty);
        using var probe = connection.CreateCommand();
        bind(probe);
        var parameters = probe.Parameters.Cast<DbParameter>().ToDictionary(p => p.ParameterName.TrimStart('@', ':'));
        if (parameters.TryGetValue("p_job_ids", out var ids))
        {
            return (long[])ids.Value!;
        }
        if (parameters.TryGetValue("p_completions", out var json))
        {
            using var batch = JsonDocument.Parse((string)json.Value!);
            return [.. batch.RootElement.EnumerateArray().Select(row => row.GetProperty("job_id").GetInt64())];
        }
        return [.. ((IEnumerable)parameters["p_batch"].Value!).Cast<IDataRecord>().Select(row => row.GetInt64(row.GetOrdinal("job_id")))];
    }
}

// Wraps the database clock operations, so the GetUtcNow skew and fault reach IActaClock too, which defaults to them.
internal sealed class FaultInjectingClock(IClockOperations inner, StoreFaultPlan plan) : IClockOperations
{
    public ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct) =>
        plan.TakeGetUtcNowFault() ? throw new InjectedProviderError("on the clock read")
        : plan.TryReadSkewedGetUtcNow(out var skewed) ? new ValueTask<DateTime>(skewed)
        : inner.GetUtcNowAsync(ct);
}

internal static class ChaosServiceCollectionExtensions
{
    public static StoreFaultPlan AddStoreFaultInjection(this IServiceCollection services)
    {
        var plan = new StoreFaultPlan();
        services.AddSingleton(plan);

        var sessionDescriptor = services.Last(d => d.ServiceType == typeof(IDbSession));
        services.Remove(sessionDescriptor);
        services.AddSingleton<IDbSession>(sp => new FaultInjectingSession(
            (IDbSession)CreateInner(sp, sessionDescriptor),
            sp.GetRequiredService<ISqlDialect>(),
            sp.GetRequiredService<StoreFaultPlan>()
        ));

        var clockDescriptor = services.Last(d => d.ServiceType == typeof(IClockOperations));
        services.Remove(clockDescriptor);
        services.AddSingleton<IClockOperations>(sp => new FaultInjectingClock(
            (IClockOperations)CreateInner(sp, clockDescriptor),
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
                sp.GetService<ActaMetrics>()
            ))
        );
        return wakeup;
    }
}

internal sealed class ControlledWakeup : IWorkerWakeup
{
    private readonly System.Threading.Lock _gate = new();
    private readonly List<(WorkerWakeupChannelKind Kind, int Threshold, TaskCompletionSource Tcs)> _wakeWaiters = [];
    private readonly Dictionary<WorkerWakeupChannelKind, int> _wakesByKind = [];
    private TaskCompletionSource? _waiting;
    private WorkerWakeupWaitStatus _nextResult = WorkerWakeupWaitStatus.TimedOut;

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
    /// Wakes published so far on channels of <paramref name="kind"/>. The instrument for an assertion
    /// that must pin a wake to the one transition publishing on that channel, rather than to any
    /// publish the surrounding arrangement also makes.
    /// </summary>
    public int WakeCountFor(WorkerWakeupChannelKind kind)
    {
        lock (_gate)
        {
            return _wakesByKind.GetValueOrDefault(kind);
        }
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

    public async ValueTask<WorkerWakeupWaitStatus> WaitAsync(WorkerWakeupChannel channel, TimeSpan timeout, CancellationToken ct)
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

    public void ReleaseWait(WorkerWakeupWaitStatus result = WorkerWakeupWaitStatus.TimedOut)
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
