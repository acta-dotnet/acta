using Acta;
using Acta.Concepts.AlertChannel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);

    // Use the namespace-plus-configure worker registration form so alert channels can be declared
    // through the worker builder.
    j.Run(
        "alert-channel",
        w =>
        {
            w.AddManifest<AlertChannelJobs>();

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
        }
    );
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

await jobs.EnqueueAsync(new CheckDiskSpace("db-server", FreePercent: 4));
await Task.Delay(800);

var queries = host.Services.GetRequiredService<IActaOperations>();
var alerts = await queries.Alerts.ListAsync(new ListJobAlertsQuery(JobNamespace: "alert-channel"));
foreach (var alert in alerts.Items)
{
    Console.WriteLine($"alert [{alert.Severity}] {alert.Title} -> channel '{alert.ChannelName}'");
}

await host.StopAsync();

namespace Acta.Concepts.AlertChannel
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
                // Route to the "ops" channel instead of the default.
                await context.AlertAsync(
                    title: $"Low disk space on {input.Server}",
                    message: $"Only {input.FreePercent}% free on {input.Server}.",
                    severityCode: AlertSeverityCode.Error,
                    channelName: "ops",
                    deduplicationKey: $"disk:{input.Server}",
                    ct: ct
                );
                Console.WriteLine($"[{input.Server}] low disk - alerted ops ({input.FreePercent}% free)");
                return;
            }

            Console.WriteLine($"[{input.Server}] disk OK ({input.FreePercent}% free)");
        }
    }
}
