using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The heartbeat re-reads the definition catalog on its first tick and whenever the namespace's newest definition
/// change the renewal answers has moved, and otherwise not at all.
/// </summary>
public sealed class WorkerHeartbeatDefinitionReloadTests
{
    [Fact]
    public async Task The_catalog_is_read_again_only_when_the_stamp_moved()
    {
        var stamp = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
        var session = new ScriptedSession(call =>
            call.Stem == "GetDefinitionContracts"
                ? Array.Empty<StoredDefinitionContract>()
                : new ExtendWorkerLeasesRow(JobId: null, ExecutionNumber: null, LockKey: null, Renewed: false, stamp)
        );
        var ct = TestContext.Current.CancellationToken;

        var heartbeat = Heartbeat(session);
        await heartbeat.TickAsync(ct);
        await heartbeat.TickAsync(ct);
        Assert.Equal(1, Reads(session));

        stamp = stamp.AddSeconds(1);
        await heartbeat.TickAsync(ct);
        await heartbeat.TickAsync(ct);
        Assert.Equal(2, Reads(session));
    }

    private static WorkerHeartbeat Heartbeat(ScriptedSession session)
    {
        var context = new WorkerContext(null);
        context.NamespaceIds["orders"] = 1;
        context.WorkerIdByNamespace["orders"] = 1;
        var services = session.Services();
        return new WorkerHeartbeat(
            services.GetRequiredService<WorkersOperations>(),
            services.GetRequiredService<DefinitionsOperations>(),
            Options.Create(new JobsOptions()),
            new WorkerRegistration("orders", null, null, [], []),
            context,
            new InFlightJobRegistry(new JobsOptions()),
            NullLogger.Instance
        );
    }

    private static int Reads(ScriptedSession session) => session.Calls.Count(call => call.Stem == "GetDefinitionContracts");
}
