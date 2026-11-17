# The whole program: cancel a job from inside its handler

The how-to is [/learn/302-cancel-job.md](/learn/302-cancel-job.md). This file holds every source file of `concepts/300-failure-and-recovery/302-cancel-job` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### cancel-job.cs

```csharp
// How to cancel a job from inside its handler
// `CancelAsync` in a handler ends the job as `Cancelled`: terminal, not a failure, so no retry and no alert follow.
using Acta;
using Acta.Concepts.CancelJob;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<CancelJobJobs>("cancel-job");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var outcome = await jobs.EnqueueAsync(new ImportFeed("feed.example.com", SourceGone: true));
await Task.Delay(500);

var snapshot = await jobs.GetAsync(outcome);
Console.WriteLine($"status={snapshot!.Status}");

await host.StopAsync();

namespace Acta.Concepts.CancelJob
{
    public sealed record ImportFeed(string Url, bool SourceGone);

    public sealed class ImportFeedJob
    {
        [Job("import-feed", MaxAttempts = 5)]
        public async Task Handle(ImportFeed feed, JobContext context, CancellationToken ct)
        {
            if (feed.SourceGone)
            {
                // learn: Cancel the job where the work is no longer wanted
                // CancelAsync ends the job as Cancelled: terminal, not a failure, so no retry and no alert.
                await context.CancelAsync("source feed no longer exists", ct);
                // learn: end
            }

            Console.WriteLine($"[{feed.Url}] imported");
        }
    }
}
```

