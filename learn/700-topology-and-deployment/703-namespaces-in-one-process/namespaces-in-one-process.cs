// How to run two namespaces in one process
// Each `Run` starts a worker for one namespace. Two of them in one process share the process and the database, and
// never each other's jobs.
using Acta;
using Acta.Learn.NamespacesInOneProcess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);

    // learn: 1. Run a worker for each namespace
    // Each Run starts an independent worker owning a distinct namespace (own claim loop, lease
    // identity, heartbeat); they share process and database but never each other's jobs.
    j.Run<NamespacesInOneProcessJobs>("orders-eu");
    j.Run<NamespacesInOneProcessJobs>("orders-us");
    // learn: end
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// A raw request targets a namespace directly. A typed enqueue of a job that two namespaces share has to name
// the namespace in its options.
for (var i = 1; i <= 3; i++)
{
    // learn: 2. Name the namespace when you enqueue
    // A request by name targets one namespace. An enqueue by type has to name the namespace in its options when
    // two namespaces run the same job.
    await jobs.EnqueueAsync(
        new EnqueueJobRequest("orders-eu", "process-order", JobPayload.Json(new ProcessOrder($"EU-{i}"))),
        CancellationToken.None
    );
    // learn: end
    await jobs.EnqueueAsync(
        new EnqueueJobRequest("orders-us", "process-order", JobPayload.Json(new ProcessOrder($"US-{i}"))),
        CancellationToken.None
    );
}

Console.WriteLine("Enqueued 3 orders into each region. Both workers drain in parallel - Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.NamespacesInOneProcess
{
    public sealed record ProcessOrder(string OrderId);

    public static class ProcessOrderJob
    {
        [Job("process-order")]
        public static void Handle(ProcessOrder input) => Console.WriteLine($"processed {input.OrderId}");
    }
}
