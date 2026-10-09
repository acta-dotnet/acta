using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// A job claimed again into a newer execution while an older attempt still runs under it in this process. The
/// heartbeat holds both executions, and the renewal answers only the one the row carries, so the older attempt is
/// cancelled instead of fed.
/// </summary>
public sealed class WorkerHeartbeatDisplacedExecutionTests
{
    [Fact]
    public async Task A_renewal_of_a_newer_execution_cancels_the_older_attempt()
    {
        var context = new WorkerContext(null);
        context.WorkerIdByNamespace["orders"] = 1;
        using var staleCts = new CancellationTokenSource();
        var stale = new RunningAttempt(staleCts) { ExecutionNumber = 1 };
        var deadlineBefore = stale.JobLeaseGoodUntil;
        context.RunningAttempts[7] = stale;
        context.BufferedClaims[7] = 2;
        var session = new ScriptedSession(_ => new ExtendWorkerLeasesRow(7, ExecutionNumber: 2, Renewed: true));
        var heartbeat = new WorkerHeartbeat(
            session.Services().GetRequiredService<WorkerLifecycle>(),
            Options.Create(new JobsOptions()),
            new WorkerRegistration("orders", null, null, [], []),
            context,
            NullLogger.Instance
        );

        await heartbeat.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            """[{"job_id":7,"execution_number":1},{"job_id":7,"execution_number":2}]""",
            Assert.Single(session.Calls).Parameters["p_executions"]
        );
        Assert.True(staleCts.IsCancellationRequested);
        Assert.Equal(deadlineBefore, stale.JobLeaseGoodUntil);
    }
}
