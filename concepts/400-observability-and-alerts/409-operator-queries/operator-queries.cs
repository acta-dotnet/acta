// Concept: IJobs provides a read-only surface for operator and dashboard reads -
// filtered lists, keyset paging, total counts, and health overview counters.

using Acta;
using Acta.Concepts.OperatorQueries;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<OperatorQueriesJobs>("operator-queries");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();
var operations = host.Services.GetRequiredService<IActaOperations>();
var queries = host.Services.GetRequiredService<IActaOperations>();

// Enqueue several jobs so there is data to query against.
Console.WriteLine("Enqueuing jobs across two definitions...");
var o1 = await jobs.EnqueueAsync(new GenerateReport("q1"));
var o2 = await jobs.EnqueueAsync(new GenerateReport("q2"));
var o3 = await jobs.EnqueueAsync(new GenerateReport("q3"));
await jobs.EnqueueAsync(new SendNotification("n1"));
await jobs.EnqueueAsync(new SendNotification("n2"));

// Cancel one job so there is a non-Ready status to filter on.
await jobs.CancelAsync(o1);

// Allow the worker to process the others.
await Task.Delay(800);

// ListJobsAsync with a namespace filter: all jobs in this namespace.
Console.WriteLine("=== ListJobsAsync (namespace filter, page size 3) ===");
var page1 = await queries.Ledger.ListJobsAsync(new ListJobsQuery(JobNamespace: "operator-queries", PageSize: 3, IncludeTotal: true));
Console.WriteLine($"Page 1: {page1.Items.Count} items, total={page1.TotalCount}, hasMore={page1.HasMore}");
foreach (var item in page1.Items)
    Console.WriteLine($"  {item.JobRef} [{item.JobName}] status={item.Status}");

// ListJobsAsync with a status filter.
Console.WriteLine("=== ListJobsAsync (status=Cancelled filter) ===");
var cancelled = await queries.Ledger.ListJobsAsync(new ListJobsQuery(JobNamespace: "operator-queries", Status: JobStatusCode.Cancelled));
Console.WriteLine($"Cancelled jobs: {cancelled.Items.Count}");
foreach (var item in cancelled.Items)
    Console.WriteLine($"  {item.JobRef} [{item.JobName}] status={item.Status}");

// ListJobsAsync keyset paging: fetch the second page via NextCursor.
Console.WriteLine("=== Keyset paging: second page via NextCursor ===");
if (page1.HasMore && page1.NextCursor is not null)
{
    var page2 = await queries.Ledger.ListJobsAsync(
        new ListJobsQuery(JobNamespace: "operator-queries", PageSize: 3, Cursor: page1.NextCursor)
    );
    Console.WriteLine($"Page 2: {page2.Items.Count} items, hasMore={page2.HasMore}");
    foreach (var item in page2.Items)
        Console.WriteLine($"  {item.JobRef} [{item.JobName}] status={item.Status}");
}
else
{
    Console.WriteLine("(All jobs fit on one page - NextCursor was null.)");
}

// IncludeTotal: total count across the filter.
Console.WriteLine("=== IncludeTotal: total across namespace ===");
var withTotal = await queries.Ledger.ListJobsAsync(new ListJobsQuery(JobNamespace: "operator-queries", PageSize: 1, IncludeTotal: true));
Console.WriteLine($"TotalCount across namespace: {withTotal.TotalCount}");

// GetOverviewAsync: one-shot health counters for the whole system.
// Schedules, workers, and alert lists also exist on IJobs; event-timeline depth lives in rung 404.
Console.WriteLine("=== GetOverviewAsync (system-wide) ===");
var overview = await operations.Ledger.GetOverviewAsync(new OverviewQuery());
Console.WriteLine($"Jobs={overview.JobCount} (system={overview.SystemJobCount}, user={overview.JobCount - overview.SystemJobCount})");
Console.WriteLine($"Ready={overview.ReadyCount} Executing={overview.ExecutingCount} Failed={overview.FailedCount}");
Console.WriteLine($"UnresolvedAlerts={overview.UnresolvedAlertCount} DeadWorkers={overview.DeadWorkerCount}");

await host.StopAsync();

namespace Acta.Concepts.OperatorQueries
{
    public sealed record GenerateReport(string ReportId);

    public sealed record SendNotification(string NotificationId);

    public static class ReportJob
    {
        [Job("generate-report")]
        public static void Handle(GenerateReport input) => Console.WriteLine($"[{input.ReportId}] report generated");
    }

    public static class NotificationJob
    {
        [Job("send-notification")]
        public static void Handle(SendNotification input) => Console.WriteLine($"[{input.NotificationId}] notification sent");
    }
}
