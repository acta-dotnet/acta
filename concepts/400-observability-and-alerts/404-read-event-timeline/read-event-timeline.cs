// Concept: read the append-only JobEvent ledger to answer "why did this happen?" across a parent-child lineage.
using Acta;
using Acta.Concepts.ReadEventTimeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ReadEventTimelineJobs>("read-event-timeline");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();
var queries = host.Services.GetRequiredService<IActaOperations>();

// Enqueue the parent; it will start a child that deliberately fails.
Console.WriteLine("enqueuing parent job...");
var parentOutcome = await jobs.EnqueueAsync(new RunReport("q4-2024"));

// Wait for the lineage to reach a terminal state.
Console.WriteLine("waiting for lineage to finish...");
JobStatusCode? parentStatus;
do
{
    await Task.Delay(200);
    parentStatus = await jobs.GetStatusAsync(parentOutcome);
} while (parentStatus is not null && !parentStatus.Value.IsTerminal);

Console.WriteLine($"parent finished with status: {parentStatus}");

// Query the full lineage event timeline scoped to the lineage root (the parent job's id).
// Events are returned newest-first; reverse to print in chronological order.
Console.WriteLine("event timeline for the lineage:");
var eventsPage = await queries.Ledger.ListEventsAsync(new ListEventsQuery(LineageRootId: parentOutcome.JobId, PageSize: 100));
var events = eventsPage.Items.Reverse().ToList();
foreach (var e in events)
{
    var from = e.FromStatus is { } f ? f.ToString() : "-";
    var to = e.ToStatus is { } t ? t.ToString() : "-";
    var reason = e.ReasonCode is { } rc ? $" reason={rc}" : "";
    var msg = e.ReasonMessage is { } m ? $" ({m})" : "";
    Console.WriteLine($"  [{e.JobRef}] {e.EventCode}  {from} -> {to}{reason}{msg}");
}

await host.StopAsync();

namespace Acta.Concepts.ReadEventTimeline
{
    public sealed record RunReport(string Period);

    public sealed record CollectData(string Period);

    public static class ReportingJobs
    {
        // Parent starts a child that gathers data; the child fails deliberately to show a failure reason on the timeline.
        [Job("run-report", MaxAttempts = 1)]
        public static async Task HandleParent(RunReport input, JobContext context, CancellationToken ct)
        {
            Console.WriteLine($"[run-report] starting data collection for {input.Period}");
            var child = await context.StartChildAsync("collect", new CollectData(input.Period), ct: ct);
            var childOutcome = await context.WaitChildAsync(child.JobId, ct);
            if (!childOutcome.Succeeded)
            {
                await context.FailAsync($"data collection failed for {input.Period}", ct);
            }
        }

        [Job("collect-data", MaxAttempts = 1)]
        public static Task HandleChild(CollectData input, JobContext context, CancellationToken ct)
        {
            Console.WriteLine($"[collect-data] data origin unavailable for {input.Period}");
            return context.FailAsync("data origin unavailable", ct);
        }
    }
}
