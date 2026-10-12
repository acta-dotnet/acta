using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// Covers the drain-reconcile race: <see cref="WorkerHeartbeat.TickAsync"/> must judge only the
/// <see cref="InFlightJobRegistry"/> entries that existed BEFORE the lease-extend query ran. An entry
/// registered while the query is in flight is absent from its answer (it was claimed too late to be
/// extended), so judging it would cancel it wrongly; the next tick picks it up instead.
/// </summary>
public sealed class WorkerHeartbeatReconcileSnapshotTests
{
    // Hang guards, not measurements: both waits are for an in-process gate the tick under test opens,
    // so only a tick that never got there reaches this bound.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task TickAsync_does_not_cancel_an_entry_registered_while_the_extend_query_is_in_flight()
    {
        var db = new GatedWorkerStore(liveJobIds: [1]);
        var context = new WorkerContext(null);
        context.WorkerIdByNamespace["orders"] = 1;
        var registry = new InFlightJobRegistry(new JobsOptions());
        var registration = new WorkerRegistration("orders", null, null, [], []);
        var options = Options.Create(new JobsOptions { HeartbeatInterval = TimeSpan.FromMilliseconds(20) });

        var heartbeat = new WorkerHeartbeat(
            db.Lifecycle(),
            ScriptedSession.Unreachable.GetRequiredService<DefinitionsOperations>(),
            options,
            registration,
            context,
            registry,
            recoveryPass: null!,
            NullLogger.Instance
        );

        var ct = TestContext.Current.CancellationToken;
        var tickTask = heartbeat.TickAsync(ct);

        // Wait until the extend query is actually in flight (blocked on the gate) before registering
        // the new entry, so it lands squarely in the gap the race exploits.
        await db.Entered.WaitAsync(HangGuard, ct);

        using var late = WorkerHeartbeatLeaseRunwayTests.Entry(2);
        registry.TryAdd(late);

        db.Release();
        await tickTask.WaitAsync(HangGuard, ct);

        Assert.False(late.AttemptToken.IsCancellationRequested);
    }

    // Blocks the lease extend on a gate so the test can register a new entry while the
    // tick is mid-flight, then releases it to answer a fixed "live" id set. WorkerHeartbeat.TickAsync
    // only ever extends leases.
    private sealed class GatedWorkerStore(IReadOnlyList<long> liveJobIds)
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _gate.SetResult();

        public WorkersOperations Lifecycle()
        {
            var session = new ScriptedSession(
                async (_, ct) =>
                {
                    _entered.TrySetResult();
                    await _gate.Task.WaitAsync(ct);
                    return liveJobIds
                        .Select(id => new ExtendWorkerLeasesRow(
                            id,
                            ExecutionNumber: 0,
                            LockKey: null,
                            Renewed: true,
                            DefinitionsModifiedAtUtc: null
                        ))
                        .ToArray();
                }
            );
            return session.Services().GetRequiredService<WorkersOperations>();
        }
    }
}
