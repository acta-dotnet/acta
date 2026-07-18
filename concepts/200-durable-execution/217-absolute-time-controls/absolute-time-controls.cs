// Concept: enqueue, sleep, and reschedule pinned to absolute DateTimeOffset instants
// rather than relative delays. Uses NextExecutionAt, ctx.SleepUntilAsync, and ctx.RescheduleUntilAsync.
using Acta;
using Acta.Concepts.AbsoluteTimeControls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<AbsoluteTimeControlsJobs>("absolute-time-controls");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();
var now = DateTimeOffset.UtcNow;

// --- Part 1: NextExecutionAt ---
// Pin the earliest claim to an absolute wall-clock instant rather than a relative delay.
// This demo uses a two-second offset so the run stays fast.
var runAt = now.AddSeconds(2);
Console.WriteLine($"Part 1 - NextExecutionAt: enqueuing GenerateReport to run at {runAt:HH:mm:ss.fff} UTC");
var report = await jobs.EnqueueAsync(new GenerateReport("q1", SleepUntil: now.AddSeconds(4)), o => o.NextExecutionAt(runAt));
Console.WriteLine($"  job {report.JobRef} is Pending; worker will not claim it before {runAt:HH:mm:ss.fff}");

// --- Part 2: RescheduleUntilAsync ---
// The polling job starts immediately, parks itself until a specific instant on the first run,
// then observes readiness and completes on the second run.
var rescheduleAt = now.AddSeconds(2);
Console.WriteLine($"Part 2 - RescheduleUntilAsync: enqueuing PollExternal; will reschedule to {rescheduleAt:HH:mm:ss.fff} UTC");
var poll = await jobs.EnqueueAsync(new PollExternal("batch-42", RescheduleUntil: rescheduleAt));

// Wait for both to reach terminal Done (poll completes ~2s out, report ~4s out after sleep)
Console.WriteLine("Waiting for jobs to complete...");
for (var i = 0; i < 60; i++)
{
    await Task.Delay(500);
    var reportStatus = await jobs.GetStatusAsync(report);
    var pollStatus = await jobs.GetStatusAsync(poll);
    if (reportStatus == JobStatusCode.Done && pollStatus == JobStatusCode.Done)
    {
        break;
    }
}

var reportSnap = await jobs.GetAsync(report);
var pollSnap = await jobs.GetAsync(poll);
Console.WriteLine($"GenerateReport final status: {reportSnap!.Status}, finished at {reportSnap.ModifiedAtUtc:HH:mm:ss.fff} UTC");
Console.WriteLine($"PollExternal final status:   {pollSnap!.Status}, finished at {pollSnap.ModifiedAtUtc:HH:mm:ss.fff} UTC");

await host.StopAsync();

namespace Acta.Concepts.AbsoluteTimeControls
{
    public sealed record GenerateReport(string Period, DateTimeOffset SleepUntil);

    public sealed record PollExternal(string BatchId, DateTimeOffset RescheduleUntil);

    public sealed class ReportJob
    {
        [Job("generate-report")]
        public async Task Handle(GenerateReport input, JobContext ctx, CancellationToken ct)
        {
            Console.WriteLine($"[generate-report] claimed at {DateTime.UtcNow:HH:mm:ss.fff} UTC");

            // SleepUntilAsync arms a named durable timer at an absolute instant; on replay after the
            // instant passes the named timer is already consumed and the handler continues.
            Console.WriteLine($"[generate-report] sleeping until absolute instant {input.SleepUntil:HH:mm:ss.fff} UTC");
            await ctx.SleepUntilAsync("report-delay", input.SleepUntil, ct: ct);

            Console.WriteLine($"[generate-report] resumed at {DateTime.UtcNow:HH:mm:ss.fff} UTC; report for {input.Period} sent");
        }
    }

    public sealed class PollExternalJob
    {
        private static int _runs;

        [Job("poll-external")]
        public async Task Handle(PollExternal input, JobContext ctx, CancellationToken ct)
        {
            var run = Interlocked.Increment(ref _runs);
            Console.WriteLine($"[poll-external] run #{run} at {DateTime.UtcNow:HH:mm:ss.fff} UTC");

            if (run == 1)
            {
                // RescheduleUntilAsync re-arms the job to an absolute instant without burning the
                // retry budget; the handler restarts from the top on the next claim.
                Console.WriteLine($"[poll-external] not ready; rescheduling to absolute instant {input.RescheduleUntil:HH:mm:ss.fff} UTC");
                await ctx.RescheduleUntilAsync(input.RescheduleUntil, "external batch not ready", ct);
            }

            Console.WriteLine($"[poll-external] batch {input.BatchId} is ready; processing complete");
        }
    }
}
