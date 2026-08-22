using Acta;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Anvil.Burst;

/// <summary>
/// The one process the certification runs in: a real Acta worker on the burst namespace, plus the enqueue
/// and operations surfaces the harness drives it through.
/// </summary>
/// <remarks>
/// <para>
/// In-process, and exactly one worker, for two reasons. The delivery cap is counted inside a transport,
/// and a transport only counts what the process it lives in sent - so the process that claims
/// <c>sys.alerts</c> has to be the process holding the counter. And the memory claim is a claim about the
/// projector's footprint, which is only readable where the projector runs.
/// </para>
/// <para>
/// The same worker also drains the seeded backlog, which is why the executor count is a flag: seeding is
/// throughput-bound and the projection phase that follows is single-slot, so the number that sizes one
/// does nothing for the other.
/// </para>
/// </remarks>
internal sealed class BurstHost : IAsyncDisposable
{
    private readonly IHost _host;
    private int _disposed;

    private BurstHost(IHost host, IJobs jobs, IActaOperations operations, CountingAlertTransport transport)
    {
        _host = host;
        Jobs = jobs;
        Operations = operations;
        Transport = transport;
    }

    /// <summary>The enqueue surface the seeder writes the backlog through.</summary>
    public IJobs Jobs { get; }

    /// <summary>The operator surface: schedule control, alert reads, ledger reads.</summary>
    public IActaOperations Operations { get; }

    /// <summary>The stand-in destination every alert in this namespace is delivered to.</summary>
    public CountingAlertTransport Transport { get; }

    public static async Task<BurstHost> StartAsync(BurstOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var transport = new CountingAlertTransport();
        var builder = Host.CreateApplicationBuilder();

        // UseLocalDatabase quiets the framework to Warning so a lab program's own output stands out. The
        // projector's drain-bound line is the exception worth hearing: it names which bound ended a pass,
        // which is the engine's own account of the number this certification measures.
        builder.Logging.AddFilter("Acta.Runtime.Modules.Alerting", LogLevel.Information);
        // The other direction, and it matters at this scale: every seeded job is MEANT to throw, and the
        // worker logs one warning with a full stack trace per failed attempt. At 100K jobs that is 100K
        // stack traces competing for the console with the numbers the run exists to print, and the writes
        // themselves show up in the seeding wall clock. Errors still come through.
        builder.Logging.AddFilter("Acta.Runtime.Modules.Execution.Workers.WorkerRuntime", LogLevel.Error);

        builder.Services.UseActa(j =>
        {
            j.UseLocalDatabase(builder.Configuration, options.Schema, options.Provider);
            j.DisableCli();
            j.UseJsonPayloads(BurstPayloadJsonContext.Default);
            j.ConfigureOptions(o =>
            {
                // After UseLocalDatabase, which caps executors at 4 for a dev box; the burst seeding phase
                // is a throughput phase and sizes itself.
                o.MaxConcurrentExecutors = options.Executors;
                o.ClaimBatchSize = options.ClaimBatch;
                o.ExecutionProfile = ExecutionProfile.Direct;
                // The harness makes the sys.alerts slot due and then waits for it; a one-second safety
                // poll bounds how long "due" takes to become "claimed" when a wake is missed.
                o.SafetyPollInterval = TimeSpan.FromSeconds(1);
                // The validator's floor. Retention is applied in whole days, so a shorter window is not
                // expressible - the stuck subset is aged past this one rather than the shipped 90.
                o.AlertRetention = BurstBounds.AlertRetention;
                // One second instead of the shipped day, and it is what makes "resolved alerts are not
                // delivered" a real check rather than a vacuous one. On the shipped interval a delivered
                // alert is not due again for 24 hours, so a run that resolved it and observed no further
                // send would have observed nothing. At one second the reminder arm re-selects every open
                // delivered incident on the very next invocation, so the harness can watch a ref being
                // re-sent, resolve it, and watch the re-sends stop.
                o.AlertReminderInterval = BurstBounds.ReminderInterval;
            });
            // Appended to the built-in log and Slack transports rather than replacing them: nothing routes
            // to those here, and a registry that lost them would stop resembling a real deployment's.
            j.Services.AddSingleton<IAlertTransport>(transport);
            j.Run(
                options.Namespace,
                w =>
                {
                    w.AddManifest<BurstJobs>();
                    // Overrides the framework's implicit "default" log channel. Every burst definition
                    // declares no channel of its own, so this is the channel the whole backlog delivers
                    // to, which is what makes one counter the answer for the whole namespace.
                    w.AddAlertChannel("default", CountingAlertTransport.Kind, "burst://counter");
                }
            );
        });

        var host = builder.Build();
        await host.StartAsync(ct);
        return new BurstHost(
            host,
            host.Services.GetRequiredService<IJobs>(),
            host.Services.GetRequiredService<IActaOperations>(),
            transport
        );
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _host.StopAsync(TimeSpan.FromSeconds(15));
        }
        catch (OperationCanceledException)
        {
            // A stop that runs out of time still has to reach Dispose below; the run is over either way.
        }

        _host.Dispose();
    }
}
