# The whole program: stop cleanly when a job is cancelled

The how-to is [/learn/008-cancellation-token.md](/learn/008-cancellation-token.md). This file holds every source file of `learn/000-fundamentals/008-cancellation-token` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### cancellation-token.cs

```csharp
// How to stop cleanly when a job is cancelled
// A handler takes a `CancellationToken` and passes it to everything it awaits, so a cancel, a timeout, or a shutdown
// that outlasts the host's grace period ends it promptly.
using Acta;
using Acta.Learn.Cancellation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<CancellationJobs>("cancellation-token");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

await jobs.EnqueueAsync(new SlowImport(3));
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.Cancellation
{
    public sealed record SlowImport(int Batches);

    public static class ImportJob
    {
        // Acta trips the CancellationToken on shutdown or lease loss; the handler must observe it.
        [Job("slow-import")]
        public static async Task Handle(SlowImport input, CancellationToken ct)
        {
            for (var batch = 1; batch <= input.Batches; batch++)
            {
                // learn: Check the token between pieces of work, and pass it to what you await
                // The token trips on an operator's cancel, at the `ExecutionTimeout`, on a lost lease, and on a shutdown
                // once the host's `ShutdownTimeout` has passed. A handler that ignores it keeps running after the
                // worker has been told to stop.
                ct.ThrowIfCancellationRequested();
                await Task.Delay(100, ct);
                // learn: end
                Console.WriteLine($"Imported batch {batch}/{input.Batches}");
            }
        }
    }
}
```

