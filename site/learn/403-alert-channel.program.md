# The whole program: send alerts to your own channel

The how-to is [/learn/403-alert-channel.md](/learn/403-alert-channel.md). This file holds every source file of `learn/400-observability-and-alerts/403-alert-channel` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### alert-channel.cs

```csharp
// How to send alerts to your own channel
// `AddAlertChannel` declares where alerts go, under a name of your choice, and an alert names the channel it is for.
using Acta;
using Acta.Learn.AlertChannel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);

    // Use the namespace-plus-configure worker registration form so alert channels can be declared
    // through the worker builder.
    j.Run("alert-channel", w =>
    {
        w.AddManifest<AlertChannelJobs>();

        // learn: 1. Declare the channel on the worker
        // `w` is the worker builder of `j.Run("alert-channel", w => { w.AddManifest<AlertChannelJobs>(); ... })`,
        // the form of `Run` that takes more than a manifest.
        // Alert channels declare process-local delivery configuration. Alert rows persist only
        // the selected channel name; endpoints and secrets stay in app configuration.
        // MinSeverity = Warning suppresses lower-severity alerts at delivery.
        w.AddAlertChannel(
            "ops",
            AlertTransportKinds.Log,
            endpoint: "ops", // the log transport ignores the endpoint; a Slack channel passes its URL here
            o =>
            {
                o.MinSeverity = AlertSeverityCode.Warning;
            }
        );
        // learn: end
    });
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

await jobs.EnqueueAsync(new CheckDiskSpace("db-server", FreePercent: 4));
await Task.Delay(800);

var queries = host.Services.GetRequiredService<IActaOperations>();
var alerts = await queries.Alerts.ListAsync(new ListAlertsQuery(JobNamespace: "alert-channel"));
foreach (var alert in alerts.Items)
{
    Console.WriteLine($"alert [{alert.Severity}] {alert.Title} -> channel '{alert.ChannelName}'");
}

await host.StopAsync();

namespace Acta.Learn.AlertChannel
{
    public sealed record CheckDiskSpace(string Server, int FreePercent);

    public sealed class CheckDiskSpaceJob
    {
        [Job("check-disk-space")]
        public async Task Handle(CheckDiskSpace input, JobContext context, CancellationToken ct)
        {
            await Task.Delay(50, ct);

            if (input.FreePercent < 10)
            {
                // learn: 2. Name the channel when you raise the alert
                // Route to the "ops" channel instead of the default.
                await context.AlertAsync(
                    title: $"Low disk space on {input.Server}",
                    message: $"Only {input.FreePercent}% free on {input.Server}.",
                    severityCode: AlertSeverityCode.Error,
                    channelName: "ops",
                    deduplicationKey: $"disk:{input.Server}",
                    ct: ct
                );
                // learn: end
                Console.WriteLine($"[{input.Server}] low disk - alerted ops ({input.FreePercent}% free)");
                return;
            }

            Console.WriteLine($"[{input.Server}] disk OK ({input.FreePercent}% free)");
        }
    }
}
```

