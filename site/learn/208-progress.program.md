# The whole program: report how far along a job is

The how-to is [/learn/208-progress.md](/learn/208-progress.md). This file holds every source file of `learn/200-durable-execution/208-progress` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### progress.cs

```csharp
// How to report how far along a job is
// `SetProgressAsync` stores how far the job has come, where a status page or the dashboard reads it while the job
// runs.
using Acta;
using Acta.Learn.Progress;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ProgressJobs>("progress");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

await jobs.EnqueueAsync(new ImportFile("customers.csv", 20));
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.Progress
{
    public sealed record ImportFile(string Name, int Rows);

    public sealed class ImportFileJob
    {
        [Job("import-file")]
        public async Task Handle(ImportFile input, JobContext context, CancellationToken ct)
        {
            for (var row = 1; row <= input.Rows; row++)
            {
                await Task.Delay(100, ct);

                var percent = row * 100 / input.Rows;

                // learn: Store the progress as the work advances
                // Writes durable state read out-of-band (the sys.progress checkpoint), not by the caller.
                await context.SetProgressAsync(percent, ct);
                // learn: end

                var filled = percent / 10;
                Console.Write($"\r[{input.Name}] [{new string('#', filled)}{new string('-', 10 - filled)}] {percent, 3}%");
            }

            Console.WriteLine();
            Console.WriteLine($"[{input.Name}] import complete");
        }
    }
}
```

