// Concept: EventRecording gates which job events are written. Audit records every event; Failures records the failed,
// orphaned, and finally failed attempts, the success that recovers from a failure (which closes the incident), and
// what an enabled alert profile reads.
using Acta;
using Acta.Concepts.EventRecording;
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
var auditEvents = await queries.Ledger.ListEventsAsync(new ListEventsQuery(JobId: auditOutcome.JobId));
Console.WriteLine($"Audit level  -> event count: {auditEvents.Items.Count}");

// Failures: suppresses job.execution-succeeded for successful runs that follow no failure; failures emit.
var failuresOutcome = await jobs.EnqueueAsync(new FailuresWork("report-b"));
await Task.Delay(500);
var failuresEvents = await queries.Ledger.ListEventsAsync(new ListEventsQuery(JobId: failuresOutcome.JobId));
Console.WriteLine(
    $"Failures level -> event count: {failuresEvents.Items.Count} (expected 0: a successful run emits nothing at Failures level)"
);

await host.StopAsync();

namespace Acta.Concepts.EventRecording
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
        // Failures: a failure writes an event, and so does the success that answers one; a run that
        // succeeds after no failure leaves none. Use it for high-frequency definitions to keep events lean.
        [Job("failures-work", EventRecording = EventRecordingCode.Failures)]
        public static void Handle(FailuresWork input) => Console.WriteLine($"[{input.Id}] running at Failures level");
    }
}
