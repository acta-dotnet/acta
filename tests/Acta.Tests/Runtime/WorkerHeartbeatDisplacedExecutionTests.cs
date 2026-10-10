using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static Acta.Tests.Runtime.WorkerHeartbeatLeaseRunwayTests;

namespace Acta.Tests.Runtime;

/// <summary>
/// A job claimed again into a newer execution while an older attempt still runs under it in this process. The
/// registry holds both executions as separate entries, and the renewal answers only the one the row carries, so the
/// older attempt is cancelled and the newer claim is kept.
/// </summary>
public sealed class WorkerHeartbeatDisplacedExecutionTests
{
    [Fact]
    public async Task A_renewal_of_a_newer_execution_cancels_the_older_attempt()
    {
        var context = new WorkerContext(null);
        context.WorkerIdByNamespace["orders"] = 1;
        var registry = new InFlightJobRegistry(new JobsOptions());
        using var stale = Entry(7, executionNumber: 1, state: InFlightJobState.Executing);
        using var newer = Entry(7, executionNumber: 2);
        registry.TryAdd(stale);
        registry.TryAdd(newer);
        var session = new ScriptedSession(_ => new ExtendWorkerLeasesRow(
            7,
            ExecutionNumber: 2,
            LockKey: null,
            Renewed: true,
            DefinitionsModifiedAtUtc: null
        ));
        var heartbeat = new WorkerHeartbeat(
            session.Services().GetRequiredService<WorkersOperations>(),
            ScriptedSession.Unreachable.GetRequiredService<DefinitionsOperations>(),
            Options.Create(new JobsOptions()),
            new WorkerRegistration("orders", null, null, [], []),
            context,
            registry,
            NullLogger.Instance
        );

        await heartbeat.TickAsync(TestContext.Current.CancellationToken);

        var executions = Assert.IsType<string>(Assert.Single(session.Calls).Parameters["p_executions"]);
        Assert.Contains("""{"job_id":7,"execution_number":1}""", executions, StringComparison.Ordinal);
        Assert.Contains("""{"job_id":7,"execution_number":2}""", executions, StringComparison.Ordinal);
        Assert.True(stale.AttemptToken.IsCancellationRequested);
        Assert.False(newer.AttemptToken.IsCancellationRequested);
    }
}
