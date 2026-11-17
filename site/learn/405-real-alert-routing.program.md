# The whole program: route alerts to Slack from configuration

The how-to is [/learn/405-real-alert-routing.md](/learn/405-real-alert-routing.md). This file holds every source file of `concepts/400-observability-and-alerts/405-real-alert-routing` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### real-alert-routing.cs

```csharp
// How to route alerts to Slack from configuration
// An alert channel posts to a Slack webhook whose URL comes from configuration, and logs when none is set. A
// deduplication key collapses repeats of one incident into one alert.
using Acta;
using Acta.Concepts.RealAlertRouting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);

    j.Run("real-alert-routing", w =>
    {
        w.AddManifest<RealAlertRoutingJobs>();

        // learn: 1. Declare a Slack channel with its URL from configuration
        // Built-in Slack incoming-webhook transport, with the webhook URL from configuration
        // (Alerts:SlackWebhookUrl). Without one the channel logs instead, so a plain run sends nothing.
        // Acta SQL stores only channel_name = "ops-slack" either way.
        // MinSeverity = Warning suppresses lower-severity alerts at delivery.
        var webhookUrl = builder.Configuration["Alerts:SlackWebhookUrl"];
        w.AddAlertChannel(
            "ops-slack",
            webhookUrl is null ? AlertTransportKinds.Log : AlertTransportKinds.SlackWebhook,
            endpoint: webhookUrl ?? "ops-slack",
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

// The shared deduplicationKey collapses both reports onto a single alert row (OccurrenceCount = 2).
await jobs.EnqueueAsync(new QueueDepthCheck("orders", Depth: 12_000));
await jobs.EnqueueAsync(new QueueDepthCheck("orders", Depth: 13_500));
await Task.Delay(1000);

var queries = host.Services.GetRequiredService<IActaOperations>();
var alerts = await queries.Alerts.ListAsync(new ListAlertsQuery(JobNamespace: "real-alert-routing"));
foreach (var alert in alerts.Items)
{
    Console.WriteLine(
        $"alert [{alert.Severity}] {alert.Title} -> '{alert.ChannelName}' x{alert.OccurrenceCount}, delivery {alert.DeliveryStatus}"
    );
}

await host.StopAsync();

namespace Acta.Concepts.RealAlertRouting
{
    public sealed record QueueDepthCheck(string Queue, int Depth);

    public sealed class QueueDepthCheckJob
    {
        [Job("queue-depth-check")]
        public async Task Handle(QueueDepthCheck input, JobContext context, CancellationToken ct)
        {
            await Task.Delay(50, ct);

            if (input.Depth > 10_000)
            {
                // learn: 2. Raise the alerts of one incident under one key
                // deduplicationKey ties repeats together onto one open incident row.
                await context.AlertAsync(
                    title: $"Queue backlog: {input.Queue}",
                    message: $"'{input.Queue}' depth is {input.Depth}, above the 10k threshold.",
                    severityCode: AlertSeverityCode.Error,
                    channelName: "ops-slack",
                    deduplicationKey: $"queue-depth:{input.Queue}",
                    ct: ct
                );
                // learn: end
                Console.WriteLine($"[{input.Queue}] depth {input.Depth} - alerted ops-slack");
                return;
            }

            Console.WriteLine($"[{input.Queue}] depth {input.Depth} OK");
        }
    }
}
```

