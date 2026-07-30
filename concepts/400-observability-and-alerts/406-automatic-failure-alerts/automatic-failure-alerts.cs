// Concept: a failing job raises an automatic alert with no ctx.AlertAsync; same-job retries
// deduplicate onto the same row, incrementing OccurrenceCount rather than opening new alerts.

using Acta;
using Acta.Concepts.AutomaticFailureAlerts;
using Acta.Querying;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);

    // The Run lambda overload lets us declare alert channels alongside the manifest.
    j.Run(
        "automatic-failure-alerts",
        w =>
        {
            w.AddManifest<AutomaticFailureAlertsJobs>();

            // The "default" log channel exists implicitly. Declaring it here overrides the implicit
            // startup configuration; no channel endpoint/config is persisted in SQL.
            w.AddAlertChannel("default", AlertTransportKinds.Log, endpoint: "default");
        }
    );
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();
var queries = host.Services.GetRequiredService<IJobs>();

Console.WriteLine("Enqueueing a job that always fails with the same reason...");
var outcome = await jobs.EnqueueAsync(new ProcessReport("monthly-2024-06"));

// Let the job fail twice (well under the default AlertFailureThreshold of 3).
// The handler delays 2 s per attempt so two failures take ~4 s; at 5 s the third
// attempt is still Executing, and CancelAsync trips its CancellationToken.
await Task.Delay(5000);

// Cancel during the third attempt to keep the lesson to the FirstFailure stage only.
// (Three or more retryable failures would trigger a separate ThresholdReached alert; a
// cancel mid-execution trips the handler's CancellationToken and ends the job as Cancelled.)
Console.WriteLine("Cancelling job to stay in the FirstFailure stage...");
await jobs.CancelAsync(outcome, "demo cancelled after two failures");

// Automatic alerts are NOT raised at failure time. The __alerts recurring job on a
// Cron.EveryMinute sweep generates them. Wait up to ~75s for the row to appear.
Console.WriteLine("waiting for the __alerts sweep...");
var deadline = DateTime.UtcNow.AddSeconds(75);
PagedResult<JobAlertListItem>? alertPage = null;
while (DateTime.UtcNow < deadline)
{
    alertPage = await queries.Alerts.ListAsync(new ListJobAlertsQuery(JobNamespace: "automatic-failure-alerts"));
    if (alertPage.Items.Count > 0)
        break;
    await Task.Delay(2000);
}

if (alertPage is null || alertPage.Items.Count == 0)
{
    Console.WriteLine("No alert arrived within the poll window.");
}
else
{
    foreach (var alert in alertPage.Items)
    {
        Console.WriteLine(
            $"alert kind={alert.Kind} severity={alert.Severity} origin={alert.Origin} "
                + $"occurrences={alert.OccurrenceCount} channel='{alert.ChannelName}' delivery={alert.DeliveryStatus}"
        );
        Console.WriteLine($"  title: {alert.Title}");
        // RunbookUrl is not on JobAlertListItem (the query projection); it is resolved at delivery
        // time onto AlertNotification.RunbookUrl. To see it, look at the delivered
        // "ACTA ALERT ... runbook=https://..." log line above, emitted by LogAlertTransport.
    }
}

await host.StopAsync();

namespace Acta.Concepts.AutomaticFailureAlerts
{
    public sealed record ProcessReport(string ReportId);

    public static class ProcessReportJob
    {
        // AlertProfile = OnFailure + AlertChannelName routes automatic alerts to the "default"
        // log channel. RunbookUrl surfaces on the delivered alert log line (LogAlertTransport prints
        // runbook=...), not on list queries.
        // Backoff = 0s skips wait between retries; the 2 s delay in the handler
        // gives the driver a window to cancel during the third attempt.
        [Job(
            "process-report",
            AlertProfile = JobAlertProfileCode.OnFailure,
            AlertChannelName = "default",
            RunbookUrl = "https://runbooks.example.com/process-report",
            MaxAttempts = 10,
            Backoff = "0s"
        )]
        public static async Task Handle(ProcessReport input, CancellationToken ct)
        {
            await Task.Delay(2000, ct);
            throw new InvalidOperationException($"Report store unavailable for '{input.ReportId}'.");
        }
    }
}
