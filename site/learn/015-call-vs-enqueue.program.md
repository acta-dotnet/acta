# The whole program: choose between starting a job and waiting for it

The how-to is [/learn/015-call-vs-enqueue.md](/learn/015-call-vs-enqueue.md). This file holds every source file of `learn/000-fundamentals/015-call-vs-enqueue` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### call-vs-enqueue.cs

```csharp
// How to choose between starting a job and waiting for it
// `EnqueueAsync` returns as soon as the job is stored; `CallAsync` enqueues, waits, and returns the result.
using Acta;
using Acta.Learn.CallVsEnqueue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<CallVsEnqueueJobs>("call-vs-enqueue");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// learn: 1. Start a job and carry on
// EnqueueAsync returns immediately, before the job runs.
var enqueued = await jobs.EnqueueAsync(new AddNumbers(1, 1));

// learn: end
Console.WriteLine($"EnqueueAsync returned right away: job {enqueued.JobRef}, status {(await enqueued.GetStatusAsync())?.Status}");

// learn: 2. Start a job and wait for its result
// CallAsync enqueues, then waits and returns the result. (See 013-read-result for the read-back path.)
var executed = await jobs.CallAsync(CallVsEnqueueJobs.AddNumbers(new AddNumbers(2, 2)));

// learn: end
Console.WriteLine($"CallAsync waited and returned: job {executed.JobId}, result {executed.ValueOrThrow().Sum}");

await host.StopAsync();

namespace Acta.Learn.CallVsEnqueue
{
    public sealed record AddNumbers(int Left, int Right);

    public sealed record AddNumbersResult(int Sum);

    public static class AddNumbersJob
    {
        [Job("add-numbers")]
        public static async Task<AddNumbersResult> Handle(AddNumbers input, CancellationToken ct)
        {
            await Task.Delay(500, ct);
            return new AddNumbersResult(input.Left + input.Right);
        }
    }
}
```

