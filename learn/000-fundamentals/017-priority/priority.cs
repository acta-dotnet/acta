// How to run urgent jobs first
// A priority on the enqueue decides which of the jobs that are due at once a worker claims first.
using Acta;
using Acta.Learn.Priority;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    // One executor at a time, so claim order is observable.
    j.ConfigureOptions(o => o.MaxConcurrentExecutors = 1);
    j.Run<PriorityJobs>("priority");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

await jobs.EnqueueAsync(new GenerateReport("bulk-1"), o => o.Priority(JobPriorityCode.Bulk));

// Due at the same instant, so priority (not arrival order) decides claim order: High beats Bulk.
// Expected order: bulk-1, URGENT, bulk-2, bulk-3, bulk-4, bulk-5.
var together = DateTimeOffset.UtcNow.AddSeconds(1.5);
foreach (var n in new[] { 2, 3, 4, 5 })
{
    await jobs.EnqueueAsync(new GenerateReport($"bulk-{n}"), o => o.Priority(JobPriorityCode.Bulk).NextRunAt(together));
}

// learn: Set the priority when you enqueue
// `Priority` is an option of the enqueue. When several jobs are due at once, the worker claims the one with the
// higher priority first, whichever arrived first. `together` is one instant a moment ahead that every job here
// shares, so they all come due at once.
await jobs.EnqueueAsync(new GenerateReport("URGENT"), o => o.Priority(JobPriorityCode.High).NextRunAt(together));

// learn: end
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.Priority
{
    public sealed record GenerateReport(string Name);

    public static class ReportJob
    {
        [Job("generate-report")]
        public static async Task Handle(GenerateReport input, CancellationToken ct)
        {
            Console.WriteLine($"running {input.Name}");
            await Task.Delay(400, ct);
        }
    }
}
