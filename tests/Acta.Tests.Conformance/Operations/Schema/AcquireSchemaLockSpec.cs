using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schema;

/// <summary>
/// Conformance for <c>AcquireSchemaLock</c>: the migration runner takes it first in its transaction, so a second runner
/// on the same schema reads no history until the first one's transaction ends.
/// </summary>
[ConformanceSpec(
    "acquire-schema-lock.excludes",
    "Two installers of one schema never hold its lock at once",
    Area = "Schema",
    Contract = "AcquireSchemaLock, taken in the migration transaction, holds every other installer of the same schema off until that transaction ends."
)]
public sealed class AcquireSchemaLockSpec : IntegrationSpec<ConformanceFixture>
{
    private const int TimeoutSeconds = 30;

    [Fact(DisplayName = "A second runner takes the lock only after the first runner's transaction commits")]
    public async Task Second_runner_waits_for_the_first()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var probe = await Fixture.CreateHistoryProbeAsync(history: null);
        var sql = new SqlResourceCatalog(Fixture.DialectToken, probe.SchemaName);
        await using var first = await Fixture.OpenConnectionAsync(probe.ConnectionString, ct);
        await using var second = await Fixture.OpenConnectionAsync(probe.ConnectionString, ct);

        await using var held = await first.BeginTransactionAsync(ct);
        await new AcquireSchemaLockHandler(first, sql, TimeoutSeconds).HandleAsync(
            new AcquireSchemaLockCommand(held, probe.SchemaName),
            ct
        );

        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Off the test thread: on SQLite the wait is the immediate transaction's begin, which blocks synchronously.
        var waiter = Task.Run(
            async () =>
            {
                await using var tx = await second.BeginTransactionAsync(ct);
                await new AcquireSchemaLockHandler(second, sql, TimeoutSeconds).HandleAsync(
                    new AcquireSchemaLockCommand(tx, probe.SchemaName),
                    ct
                );
                var afterRelease = released.Task.IsCompleted;
                await tx.CommitAsync(ct);
                return afterRelease;
            },
            ct
        );

        // Time for the second runner to reach the lock: a lock that let it through would be taken before the release.
        await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        released.SetResult();
        await held.CommitAsync(ct);

        Assert.True(await waiter.WaitAsync(SpecWaits.Gate, ct), "The second runner took the schema lock while the first still held it.");
    }
}
