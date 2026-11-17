# The whole program: run urgent jobs first

The how-to is [/learn/017-priority.md](/learn/017-priority.md). This file holds every source file of `learn/000-fundamentals/017-priority` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### priority.cs

```csharp
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
// higher priority first, whichever arrived first.
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
```

