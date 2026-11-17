# The whole program: run two namespaces in one process

The how-to is [/learn/703-namespaces-in-one-process.md](/learn/703-namespaces-in-one-process.md). This file holds every source file of `learn/700-topology-and-deployment/703-namespaces-in-one-process` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### namespaces-in-one-process.cs

```csharp
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

// A raw request targets a namespace directly; typed enqueue can't disambiguate two namespaces that
// share the same job.
for (var i = 1; i <= 3; i++)
{
    // learn: 2. Name the namespace when you enqueue
    // A request by name targets one namespace. An enqueue by type could not tell two namespaces with the same job
    // apart.
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
```

