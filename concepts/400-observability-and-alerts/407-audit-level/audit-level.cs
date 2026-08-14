// Concept: AuditLevel gates which job events are written; Audit emits everything, Failures emits only
// failure outcomes, and Off suppresses all audit-filtered per-job events.
using Acta;
using Acta.Concepts.AuditLevel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<AuditLevelJobs>("audit-level");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();
var queries = host.Services.GetRequiredService<IActaOperations>();

// Audit: emits job.execution-started and job.execution-finished for every run.
var auditOutcome = await jobs.EnqueueAsync(new AuditWork("report-a"));
await Task.Delay(500);
var auditEvents = await queries.Ledger.ListEventsAsync(new ListEventsQuery(JobId: auditOutcome.JobId));
Console.WriteLine($"Audit level  -> event count: {auditEvents.Items.Count}");

// Failures: suppresses started/finished for successful runs; only failure events emit.
var failuresOutcome = await jobs.EnqueueAsync(new FailuresWork("report-b"));
await Task.Delay(500);
var failuresEvents = await queries.Ledger.ListEventsAsync(new ListEventsQuery(JobId: failuresOutcome.JobId));
Console.WriteLine(
    $"Failures level -> event count: {failuresEvents.Items.Count} (expected 0: a successful run emits nothing at Failures level)"
);

// Off: suppresses all audit-filtered per-job events; the job runs but the ledger stays silent.
var offOutcome = await jobs.EnqueueAsync(new OffWork("report-c"));
await Task.Delay(500);
var offEvents = await queries.Ledger.ListEventsAsync(new ListEventsQuery(JobId: offOutcome.JobId));
Console.WriteLine($"Off level    -> event count: {offEvents.Items.Count}");

await host.StopAsync();

namespace Acta.Concepts.AuditLevel
{
    public sealed record AuditWork(string Id);

    public sealed record FailuresWork(string Id);

    public sealed record OffWork(string Id);

    public static class AuditWorkJob
    {
        // Audit (default): both started and finished events are written for every execution.
        [Job("audit-work", AuditLevel = JobAuditLevelCode.Audit)]
        public static void Handle(AuditWork input) => Console.WriteLine($"[{input.Id}] running at Audit level");
    }

    public static class FailuresWorkJob
    {
        // Failures: only failure outcomes write an event; a successful run leaves no events.
        [Job("failures-work", AuditLevel = JobAuditLevelCode.Failures)]
        public static void Handle(FailuresWork input) => Console.WriteLine($"[{input.Id}] running at Failures level");
    }

    public static class OffWorkJob
    {
        // Off: no audit-filtered events at all; use for high-frequency definitions to keep events lean.
        [Job("off-work", AuditLevel = JobAuditLevelCode.Off)]
        public static void Handle(OffWork input) => Console.WriteLine($"[{input.Id}] running at Off level");
    }
}
