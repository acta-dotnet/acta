using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The heartbeat chains the recovery passes it takes so one runs at a time, and nothing a pass throws, its metric
/// listener included, stops a later pass from running or the loop from stopping cleanly.
/// </summary>
public sealed class WorkerHeartbeatRecoveryChainTests
{
    [Fact]
    public async Task A_throwing_recovery_metric_listener_never_stops_a_later_pass()
    {
        var reclaims = 0;
        var services = new ScriptedSession(call =>
            call.Stem switch
            {
                // Every beat takes the namespace's recovery pass.
                "ExtendWorkerLeases" => new ExtendWorkerLeasesRow(null, null, null, Renewed: false, null, RecoveryClaimed: true),
                "MarkDeadWorkers" => 0,
                "ReclaimStuckJobs" => Reclaim(),
                "GetStaleChildLatches" => Array.Empty<GetStaleChildLatchesItem>(),
                _ => throw new NotSupportedException(call.Stem),
            }
        ).Services();

        using var metrics = new ActaMetrics();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter, metrics.Meter) && instrument.Name == "acta.recovery.passes")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => throw new InvalidOperationException("synthetic listener fault"));
        listener.Start();

        var context = new WorkerContext(null) { RunsRecovery = true };
        context.NamespaceIds["orders"] = 1;
        context.WorkerIdByNamespace["orders"] = 1;
        var heartbeat = new WorkerHeartbeat(
            services.GetRequiredService<WorkersOperations>(),
            services.GetRequiredService<DefinitionsOperations>(),
            Options.Create(new JobsOptions { HeartbeatInterval = TimeSpan.FromMilliseconds(20) }),
            new WorkerRegistration("orders", null, null, [], []),
            context,
            new InFlightJobRegistry(new JobsOptions()),
            services.GetRequiredService<RecoveryPass>(),
            NullLogger.Instance,
            metrics
        );

        using var cts = new CancellationTokenSource();
        var running = heartbeat.RunAsync(cts.Token);

        // A hang guard, not a measurement: only a chain the first pass's listener fault poisoned stops at one.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Volatile.Read(ref reclaims) < 2)
        {
            Assert.True(DateTime.UtcNow < deadline, "no recovery pass ran after the first one's metric listener threw.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await cts.CancelAsync();
        await running;

        ReclaimStuckJobsRow[] Reclaim()
        {
            Interlocked.Increment(ref reclaims);
            return [];
        }
    }
}
