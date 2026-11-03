using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Scenarios;
using Xunit;

[assembly: AssemblyFixture(typeof(DeadlockCensusStart))]

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>The server's deadlock count when the run starts, read before any test touches the database.</summary>
public sealed class DeadlockCensusStart : IAsyncLifetime
{
    public long? Count { get; private set; }

    public async ValueTask InitializeAsync() => Count = await new ConformanceFixture().ReadDeadlockCountAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>The census's own collection, with parallelization off, which xUnit runs after every parallel one.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DeadlockCensusLast
{
    public const string Name = "deadlock-census";
}

/// <summary>
/// A test never retries a deadlock, and one inside the engine's own loops is absorbed without failing any test, so the
/// run counts them on the server: it passes only while the counter it found at the start has not moved.
/// </summary>
[ConformanceSpec(
    "suite.no-deadlocks",
    "The suite runs without a deadlock",
    Area = "Testing",
    Contract = "A full conformance run leaves the server's deadlock counter where it found it, since a deadlock is fixed at its lock order and never retried."
)]
[Collection(DeadlockCensusLast.Name)]
public sealed class DeadlockCensusSpec(DeadlockCensusStart start)
{
    [Fact(DisplayName = "The server records no deadlock during the run")]
    public async Task The_run_records_no_deadlock()
    {
        Assert.SkipWhen(start.Count is null, "SQLite runs one writer at a time, so no deadlock can form.");
        var end = await new ConformanceFixture().ReadDeadlockCountAsync();
        Assert.True(
            end == start.Count,
            $"The server recorded {end - start.Count} deadlock(s) during the run. Read the graphs in system_health's "
                + "xml_deadlock_report events (SQL Server) or the server log (PostgreSQL), and fix the lock order."
        );
    }
}
