# The whole program: pass typed input to a job

The how-to is [/learn/002-job-input.md](/learn/002-job-input.md). This file holds every source file of `learn/000-fundamentals/002-job-input` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### job-input.cs

```csharp
// How to pass typed input to a job
// The input is a record: Acta stores it with the job and hands it to the handler as its first parameter.
using Acta;
using Acta.Learn.JobInput;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<JobInputJobs>("job-input");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// learn: 2. Enqueue an instance of it
// The type of the value picks the job: the handler whose first parameter is a `PrintNameTag`.
await jobs.EnqueueAsync(new PrintNameTag("Ada", "workshop"));

// learn: end
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.JobInput
{
    // learn: 1. Write the input as a record
    // The input is plain data. Acta serializes it, stores it with the job, and hands it to the handler.
    public sealed record PrintNameTag(string Name, string Workshop);

    // learn: end

    public static class PrintNameTagJob
    {
        [Job("print-name-tag")]
        public static async Task Handle(PrintNameTag tag, CancellationToken ct)
        {
            await Task.Delay(50, ct);
            Console.WriteLine($"Printed name tag: {tag.Name} ({tag.Workshop})");
        }
    }
}
```

