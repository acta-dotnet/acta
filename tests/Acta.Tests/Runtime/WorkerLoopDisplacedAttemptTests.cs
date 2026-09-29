using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Workers;
using Xunit;
using static Acta.Tests.Runtime.WorkerLoopClaimFailureTests;

namespace Acta.Tests.Runtime;

/// <summary>
/// A claim that returns a job this process is still running means the running attempt's lease lapsed
/// and the job was reclaimed. The replacement shares the stale attempt's key in <c>RunningAttempts</c>,
/// so the heartbeat would renew the stale attempt on the replacement's lease and the watchdog would never
/// cancel it. Both loop shapes cancel the stale attempt as the claim answers.
/// </summary>
public sealed class WorkerLoopDisplacedAttemptTests
{
    private const long JobId = 7;

    [Theory]
    [InlineData(ExecutionProfile.Buffered)]
    [InlineData(ExecutionProfile.Direct)]
    public async Task A_claim_of_a_job_this_process_still_runs_cancels_the_stale_attempt(ExecutionProfile profile)
    {
        var context = new WorkerContext(new WorkerRegistration("orders", null, null, [], []));
        using var staleCts = new CancellationTokenSource();
        context.RunningAttempts[JobId] = new RunningAttempt(staleCts);

        // The first claim returns the job again; the second stops the loop, so the run ends on its own.
        using var cts = new CancellationTokenSource();
        var claims = 0;
        var store = new ClaimStore(_ =>
        {
            if (++claims == 1)
            {
                return new ClaimResult([Reclaimed()], null);
            }
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var loop = Loop(store, new RecordingLogger(), profile, TimeSpan.FromMinutes(10), context);

        await loop.RunLoopAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.True(staleCts.IsCancellationRequested);
    }

    private static ClaimedJob Reclaimed() =>
        new(
            JobId: JobId,
            JobRef: Guid.CreateVersion7(),
            NamespaceId: 1,
            DefinitionId: 1,
            TenantId: null,
            ExecutionNumber: 2,
            DeduplicationKey: null,
            CorrelationKey: null,
            ConcurrencyKey: null,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            NextRunAtUtc: null,
            LeaseExpiresAtUtc: DateTime.UtcNow.AddMinutes(3),
            CreatedAtUtc: DateTime.UtcNow,
            FailureCount: 0,
            Version: 1
        );
}
