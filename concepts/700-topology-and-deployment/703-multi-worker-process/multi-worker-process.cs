using Acta;
using Acta.Concepts.MultiWorker;
using Acta.Payloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);

    // Each Run starts an independent worker owning a distinct namespace (own claim loop, lease
    // identity, heartbeat); they share process and database but never each other's jobs.
    j.Run<MultiWorkerJobs>("orders-eu");
    j.Run<MultiWorkerJobs>("orders-us");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// A raw request targets a namespace directly; typed enqueue can't disambiguate two namespaces that
// share the same job.
for (var i = 1; i <= 3; i++)
{
    await jobs.EnqueueAsync(
        new JobEnqueueRequest("orders-eu", "process-order", JobPayload.Json(new ProcessOrder($"EU-{i}"))),
        CancellationToken.None
    );
    await jobs.EnqueueAsync(
        new JobEnqueueRequest("orders-us", "process-order", JobPayload.Json(new ProcessOrder($"US-{i}"))),
        CancellationToken.None
    );
}

Console.WriteLine("Enqueued 3 orders into each region. Both workers drain in parallel - Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.MultiWorker
{
    public sealed record ProcessOrder(string OrderId);

    public static class ProcessOrderJob
    {
        [Job("process-order")]
        public static void Handle(ProcessOrder input) => Console.WriteLine($"processed {input.OrderId}");
    }
}
