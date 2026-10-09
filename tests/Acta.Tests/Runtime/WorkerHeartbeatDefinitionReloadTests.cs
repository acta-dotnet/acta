using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The heartbeat re-reads the definition catalog on its first tick, when the namespace's newest definition change
/// the renewal answers has moved, and once <see cref="JobsOptions.SafetyPollInterval"/> has passed since the last read.
/// </summary>
public sealed class WorkerHeartbeatDefinitionReloadTests
{
    [Fact]
    public async Task The_catalog_is_read_again_only_when_the_stamp_moved_or_the_interval_elapsed()
    {
        var stamp = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
        var session = new ScriptedSession(call =>
            call.Stem == "GetDefinitionContracts"
                ? Array.Empty<StoredDefinitionContract>()
                : new ExtendWorkerLeasesRow(JobId: null, ExecutionNumber: null, Renewed: false, stamp)
        );
        var ct = TestContext.Current.CancellationToken;

        var hourly = Heartbeat(session, TimeSpan.FromHours(1));
        await hourly.TickAsync(ct);
        await hourly.TickAsync(ct);
        Assert.Equal(1, Reads(session));

        stamp = stamp.AddSeconds(1);
        await hourly.TickAsync(ct);
        Assert.Equal(2, Reads(session));

        var elapsed = Heartbeat(session, TimeSpan.Zero);
        await elapsed.TickAsync(ct);
        await elapsed.TickAsync(ct);
        Assert.Equal(4, Reads(session));
    }

    private static WorkerHeartbeat Heartbeat(ScriptedSession session, TimeSpan safetyPoll)
    {
        var context = new WorkerContext(null);
        context.NamespaceIds["orders"] = 1;
        context.WorkerIdByNamespace["orders"] = 1;
        var services = session.Services();
        return new WorkerHeartbeat(
            services.GetRequiredService<WorkerLifecycle>(),
            services.GetRequiredService<GetDefinitionContractsHandler>(),
            Options.Create(new JobsOptions { SafetyPollInterval = safetyPoll }),
            new WorkerRegistration("orders", null, null, [], []),
            context,
            NullLogger.Instance
        );
    }

    private static int Reads(ScriptedSession session) => session.Calls.Count(call => call.Stem == "GetDefinitionContracts");
}
