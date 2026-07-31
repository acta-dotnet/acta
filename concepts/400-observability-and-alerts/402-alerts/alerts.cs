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
var alerts = await queries.Alerts.ListAsync(new ListJobAlertsQuery(JobNamespace: "alerts"));
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
                // No channel name routes to the implicit "default" channel (log transport). The
                // database stores only the channel name; delivery configuration comes from worker startup.
                await context.AlertAsync(
                    title: $"Backup verification failed: {input.FolderName}",
                    message: $"The nightly backup of '{input.FolderName}' is missing or corrupt.",
                    severityCode: AlertSeverityCode.Error,
                    deduplicationKey: $"backup-failed:{input.FolderName}",
                    ct: ct
                );
                Console.WriteLine($"[{input.FolderName}] backup is bad - alert raised");
                return;
            }

            Console.WriteLine($"[{input.FolderName}] backup verified OK");
        }
    }
}
