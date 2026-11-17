# The whole program: return a result and wait for it

The how-to is [/learn/003-job-result.md](/learn/003-job-result.md). This file holds every source file of `learn/000-fundamentals/003-job-result` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### job-result.cs

```csharp
// How to return a result and wait for it
// What the handler returns is stored with the job as its result. `WaitAsync<T>` follows the job to its end and hands
// the result back, typed.
using Acta;
using Acta.Learn.JobResult;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<JobResultJobs>("job-result");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var handle = await jobs.EnqueueAsync(new AddNumbers(2, 3));

// learn: 2. Wait for the job and read its result
// WaitAsync follows the job to its terminal state and carries the typed result.
var result = (await handle.WaitAsync<AddNumbersResult>()).ValueOrThrow();
Console.WriteLine($"2 + 3 = {result.Sum}");

// learn: end

await host.StopAsync();

namespace Acta.Learn.JobResult
{
    public sealed record AddNumbers(int Left, int Right);

    public sealed record AddNumbersResult(int Sum);

    public static class AddNumbersJob
    {
        // learn: 1. Return the result from the handler
        // What the handler returns is stored with the job as its result.
        [Job("add-numbers")]
        public static AddNumbersResult Handle(AddNumbers input) => new(input.Left + input.Right);
        // learn: end
    }
}
```

