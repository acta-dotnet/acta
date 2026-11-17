# The whole program: raise an alert from a handler

The how-to is [/learn/402-alerts.md](/learn/402-alerts.md). This file holds every source file of `concepts/400-observability-and-alerts/402-alerts` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### alerts.cs

```csharp
// How to raise an alert from a handler
// `AlertAsync` in a handler raises an alert for the people who operate the system, and `operations.Alerts` reads it
// back.
using Acta;
using Acta.Concepts.Alerts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<AlertsJobs>("alerts");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// Handler raises an operator alert; read it back through the read-only operator query surface.
await jobs.EnqueueAsync(new VerifyBackup("photos", Healthy: false));
await Task.Delay(800);

var queries = host.Services.GetRequiredService<IActaOperations>();

// learn: 2. Read the alerts back
// An alert is a row: the list answers its severity, its channel, and whether it was delivered.
var alerts = await queries.Alerts.ListAsync(new ListAlertsQuery(JobNamespace: "alerts"));

// learn: end
foreach (var alert in alerts.Items)
{
    Console.WriteLine($"alert [{alert.Severity}] {alert.Title} -> channel '{alert.ChannelName}', delivery {alert.DeliveryStatus}");
}

await host.StopAsync();

namespace Acta.Concepts.Alerts
{
    public sealed record VerifyBackup(string FolderName, bool Healthy);

    public sealed class VerifyBackupJob
    {
        [Job("verify-backup")]
        public async Task Handle(VerifyBackup input, JobContext context, CancellationToken ct)
        {
            await Task.Delay(50, ct);

            if (!input.Healthy)
            {
                // learn: 1. Raise the alert from the handler
                // No channel name routes to the implicit "default" channel (log transport). The
                // database stores only the channel name; delivery configuration comes from worker startup.
                await context.AlertAsync(
                    title: $"Backup verification failed: {input.FolderName}",
                    message: $"The nightly backup of '{input.FolderName}' is missing or corrupt.",
                    severityCode: AlertSeverityCode.Error,
                    deduplicationKey: $"backup-failed:{input.FolderName}",
                    ct: ct
                );
                // learn: end
                Console.WriteLine($"[{input.FolderName}] backup is bad - alert raised");
                return;
            }

            Console.WriteLine($"[{input.FolderName}] backup verified OK");
        }
    }
}
```

