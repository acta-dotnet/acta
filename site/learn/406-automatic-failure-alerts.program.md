# The whole program: get an alert when a job fails

The how-to is [/learn/406-automatic-failure-alerts.md](/learn/406-automatic-failure-alerts.md). This file holds every source file of `learn/400-observability-and-alerts/406-automatic-failure-alerts` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### automatic-failure-alerts.cs

```csharp
// How to get an alert when a job fails
// A failing job raises an alert by itself, with no `AlertAsync` in the handler. `AlertProfile` on the job says when,
// and repeats of the same job's failure count up on one alert rather than opening new ones.

using Acta;
using Acta.Learn.AutomaticFailureAlerts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);

    // The Run lambda overload lets us declare alert channels alongside the manifest.
    j.Run("automatic-failure-alerts", w =>
    {
        w.AddManifest<AutomaticFailureAlertsJobs>();

        // The "default" log channel exists implicitly. Declaring it here overrides the implicit
        // startup configuration; no channel endpoint/config is persisted in SQL.
        w.AddAlertChannel("default", AlertTransportKinds.Log, endpoint: "default");
    });
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();
var queries = host.Services.GetRequiredService<IActaOperations>();

Console.WriteLine("Enqueueing a job that always fails with the same reason...");
var outcome = await jobs.EnqueueAsync(new ProcessReport("monthly-2024-06"));

// Let the job fail twice (well under the default AlertFailureThreshold of 3).
// The handler delays 2 s per attempt so two failures take ~4 s; at 5 s the third
// attempt is still Executing.
await Task.Delay(5000);

// Cancel during the third attempt to keep the lesson to the FirstFailure stage only.
// (Three or more retryable failures would trigger a separate ThresholdReached alert. The cancel
// ends the job as Cancelled at once; the third attempt's own throw lands after it and changes nothing.)
Console.WriteLine("Cancelling job to stay in the FirstFailure stage...");
await jobs.CancelAsync(outcome, "demo cancelled after two failures");

// Automatic alerts are NOT raised at failure time. The sys.alerts permanent job on a
// Cron.EveryMinute sweep generates them, and a sweep reads only events older than two command
// timeouts (60 s by default), so the row appears one to two minutes after the failure. A sweep
// that falls between the two failures crossing that age raises the alert with one occurrence and
// the next sweep adds the second, so the poll waits until the alert shows both.
Console.WriteLine("waiting for the sys.alerts sweep, one to two minutes...");
var deadline = DateTime.UtcNow.AddSeconds(150);
ListAlertsResponse? alertPage = null;
while (DateTime.UtcNow < deadline)
{
    alertPage = await queries.Alerts.ListAsync(new ListAlertsQuery(JobNamespace: "automatic-failure-alerts"));
    if (alertPage.Items.Any(alert => alert.OccurrenceCount >= 2))
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
            $"alert kind={alert.Kind} severity={alert.Severity} "
                + $"occurrences={alert.OccurrenceCount} channel='{alert.ChannelName}' delivery={alert.DeliveryStatus}"
        );
        Console.WriteLine($"  title: {alert.Title}");
        // RunbookUrl is not on ListAlertsItem (the query projection); it is resolved at delivery
        // time onto AlertNotification.RunbookUrl. To see it, look at the delivered
        // "Acta alert ... runbook=https://..." log line above, emitted by LogAlertTransport.
    }
}

await host.StopAsync();

namespace Acta.Learn.AutomaticFailureAlerts
{
    public sealed record ProcessReport(string ReportId);

    public static class ProcessReportJob
    {
        // learn: Say on the job when its failures alert, and where
        // AlertProfile = OnFailure + AlertChannelName routes automatic alerts to the "default"
        // log channel. RunbookUrl surfaces on the delivered alert log line (LogAlertTransport prints
        // runbook=...), not on list queries.
        // Backoff = 0s skips wait between retries; the 2 s delay in the handler
        // gives the driver a window to cancel during the third attempt.
        [Job(
            "process-report",
            AlertProfile = AlertProfileCode.OnFailure,
            AlertChannelName = "default",
            RunbookUrl = "https://runbooks.example.com/process-report",
            MaxAttempts = 10,
            Backoff = "0s"
        )]
        // learn: end
        public static async Task Handle(ProcessReport input, CancellationToken ct)
        {
            await Task.Delay(2000, ct);
            throw new InvalidOperationException($"Report store unavailable for '{input.ReportId}'.");
        }
    }
}
```

