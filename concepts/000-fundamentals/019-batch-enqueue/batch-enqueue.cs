using System.Diagnostics;
using Acta;
using Acta.Concepts.BatchEnqueue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<BatchEnqueueJobs>("batch-enqueue");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// EnqueueBatchAsync writes the whole batch in one round-trip; outcomes return in request order.
var requests = Enumerable.Range(1, 500).Select(_ => new JobEnqueueRequest("batch-enqueue", "ping")).ToList();

var sw = Stopwatch.StartNew();
var outcomes = await jobs.EnqueueBatchAsync(requests);
sw.Stop();
Console.WriteLine(
    $"Enqueued {outcomes.Count} jobs in one round-trip in {sw.ElapsedMilliseconds} ms (ids {outcomes[0].JobId}..{outcomes[^1].JobId})."
);
Console.WriteLine("Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.BatchEnqueue
{
    public static class PingJob
    {
        [Job("ping")]
        public static void Handle() { }
    }
}
