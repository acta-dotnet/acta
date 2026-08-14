using Acta;
using Acta.Concepts.RealAlertRouting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);

    j.Run(
        "real-alert-routing",
        w =>
        {
            w.AddManifest<RealAlertRoutingJobs>();

            // Built-in Slack incoming-webhook transport; endpoint is the webhook URL supplied from
            // startup configuration. Acta SQL stores only channel_name = "ops-slack".
            // MinSeverity = Warning suppresses lower-severity alerts at delivery.
            w.AddAlertChannel(
                "ops-slack",
                AlertTransportKinds.SlackWebhook,
                endpoint: "https://hooks.slack.com/services/T000/B000/XXXXXXXXXXXX", // placeholder webhook URL
                o =>
                {
                    o.MinSeverity = AlertSeverityCode.Warning;
                }
            );
        }
    );
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
                // deduplicationKey ties repeats together within the dedupe window.
                await context.AlertAsync(
                    title: $"Queue backlog: {input.Queue}",
                    message: $"'{input.Queue}' depth is {input.Depth}, above the 10k threshold.",
                    severityCode: AlertSeverityCode.Error,
                    channelName: "ops-slack",
                    deduplicationKey: $"queue-depth:{input.Queue}",
                    ct: ct
                );
                Console.WriteLine($"[{input.Queue}] depth {input.Depth} - alerted ops-slack");
                return;
            }

            Console.WriteLine($"[{input.Queue}] depth {input.Depth} OK");
        }
    }
}
