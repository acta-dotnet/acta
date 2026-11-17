# The whole program: choose how many events a job records

The how-to is [/learn/407-event-recording.md](/learn/407-event-recording.md). This file holds every source file of `learn/400-observability-and-alerts/407-event-recording` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### event-recording.cs

```csharp
// How to choose how many events a job records
// `EventRecording` on the job decides which events it writes. `Audit`, the default, records every event. `Failures`
// records the failed and orphaned attempts, the success that recovers from a failure, what an enabled alert profile
// reads, a caller's controls of the job, and a schedule's pauses and manual triggers.
using Acta;
using Acta.Learn.EventRecording;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<EventRecordingJobs>("event-recording");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();
var queries = host.Services.GetRequiredService<IActaOperations>();

// Audit: emits an attempt event (job.execution-succeeded, -failed, ...) for every run.
var auditOutcome = await jobs.EnqueueAsync(new AuditWork("report-a"));
await Task.Delay(500);
var auditEvents = await queries.Ledger.ListEventsAsync(new ListEventsQuery(Job: auditOutcome));
Console.WriteLine($"Audit level  -> event count: {auditEvents.Items.Count}");

// Failures: suppresses job.execution-succeeded for successful runs that follow no failure; failures emit.
var failuresOutcome = await jobs.EnqueueAsync(new FailuresWork("report-b"));
await Task.Delay(500);
var failuresEvents = await queries.Ledger.ListEventsAsync(new ListEventsQuery(Job: failuresOutcome));
Console.WriteLine(
    $"Failures level -> event count: {failuresEvents.Items.Count} (expected 0: a successful run emits nothing at Failures level)"
);

await host.StopAsync();

namespace Acta.Learn.EventRecording
{
    public sealed record AuditWork(string Id);

    public sealed record FailuresWork(string Id);

    public static class AuditWorkJob
    {
        // Audit (default): a finished event is written for every execution.
        [Job("audit-work", EventRecording = EventRecordingCode.Audit)]
        public static void Handle(AuditWork input) => Console.WriteLine($"[{input.Id}] running at Audit level");
    }

    public static class FailuresWorkJob
    {
        // learn: Record only failures for a job that runs often
        // Failures: a failure writes an event, and so does the success that answers one; a run that
        // succeeds after no failure leaves none. Use it for high-frequency definitions to keep events lean.
        // The program also runs `audit-work` at the default level and counts the events of both.
        [Job("failures-work", EventRecording = EventRecordingCode.Failures)]
        // learn: end
        public static void Handle(FailuresWork input) => Console.WriteLine($"[{input.Id}] running at Failures level");
    }
}
```

