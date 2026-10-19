using System.Collections.ObjectModel;
using Acta;

namespace TestJobs;

/// <summary>Records, in order, the runs and disposals the handler probes receive.</summary>
public sealed class DisposalLog
{
    public Collection<string> Calls { get; } = [];
}

/// <summary>Probe for handler activation: answers with the base address of the HttpClient it was built with.</summary>
public sealed class TypedClientProbe(HttpClient http)
{
    [Job("typed-client-probe")]
    public string Run() => http.BaseAddress?.ToString() ?? "no base address";
}

/// <summary>Probe for handler disposal on a throwing attempt.</summary>
public sealed class DisposableProbe(DisposalLog log) : IDisposable
{
    [Job("disposable-probe")]
    public void Run()
    {
        log.Calls.Add("Run");
        throw new InvalidOperationException("The disposable probe always throws.");
    }

    public void Dispose() => log.Calls.Add("Dispose");
}

/// <summary>Probe for handler disposal on a type that offers both disposals.</summary>
public sealed class AsyncDisposableProbe(DisposalLog log) : IDisposable, IAsyncDisposable
{
    [Job("async-disposable-probe")]
    public Task Run()
    {
        log.Calls.Add("Run");
        return Task.CompletedTask;
    }

    public void Dispose() => log.Calls.Add("Dispose");

    public ValueTask DisposeAsync()
    {
        log.Calls.Add("DisposeAsync");
        return ValueTask.CompletedTask;
    }
}
