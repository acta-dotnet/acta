# The whole program: limit how many times a job retries

The how-to is [/learn/016-max-attempts.md](/learn/016-max-attempts.md). This file holds every source file of `concepts/000-fundamentals/016-max-attempts` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### max-attempts.cs

```csharp
// How to limit how many times a job retries
// `MaxAttempts` on the job is its retry budget. When the last attempt fails, the job ends as `Failed`.
using Acta;
using Acta.Concepts.MaxAttempts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<MaxAttemptsJobs>("max-attempts");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// learn: 2. Read how the job ended
// The handler always throws; Acta makes up to MaxAttempts attempts, then transitions the job to Failed.
var outcome = await jobs.EnqueueAsync(new FlakyWork("widget"));
await Task.Delay(1500);
var snapshot = await jobs.GetAsync(outcome);
Console.WriteLine($"status={snapshot!.Status} failureCount={snapshot.FailureCount}");

// learn: end

await host.StopAsync();

namespace Acta.Concepts.MaxAttempts
{
    public sealed record FlakyWork(string Item);

    public static class FlakyWorkJob
    {
        // learn: 1. Set the retry budget on the job
        // MaxAttempts caps attempts, the first included; "0s" skips backoff. After the last attempt the job ends Failed.
        [Job("flaky-work", MaxAttempts = 3, Backoff = "0s")]
        public static Task Handle(FlakyWork input) => throw new InvalidOperationException($"Could not process {input.Item}.");
        // learn: end
    }
}
```

