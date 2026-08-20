using Acta;

namespace TestJobs;

/// <summary>
/// Deliberately empty payload: the drain specs prove exactly-once execution by count, and the empty
/// record keeps the enqueue-dispatch route and serializer path intact without carrying data nobody
/// reads.
/// </summary>
public sealed record LoadEcho;

public sealed record LoadEchoResult(int Ok);

/// <summary>
/// Per-run counter of executed load-echo jobs. Registered as a per-spec singleton, so parallel
/// provider specs count independently; the count doubles as the exactly-once proof (one increment
/// per handler invocation).
/// </summary>
public sealed class LoadExecutionCounter
{
    private int _executions;

    /// <summary>Executed load-echo handler invocations, one per job that actually ran.</summary>
    public int Executions => Volatile.Read(ref _executions);

    public void Record() => Interlocked.Increment(ref _executions);
}

/// <summary>
/// Backlog-drain handler: counts that it ran, then returns. Allocation-light so the drain specs
/// measure framework plus DB cost, not handler work.
/// </summary>
public sealed class LoadEchoHandler(LoadExecutionCounter counter)
{
    [Job("load-echo")]
    public LoadEchoResult Run(LoadEcho input)
    {
        counter.Record();
        return new LoadEchoResult(1);
    }
}
