# The whole program: read a job's result

The how-to is [/learn/013-read-result.md](/learn/013-read-result.md). This file holds every source file of `learn/000-fundamentals/013-read-result` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### read-result.cs

```csharp
// How to read a job's result
// `TryGetResultAsync<T>` finds nothing before Succeeded; `GetResultAsync<T>` returns the result you expect.
using Acta;
using Acta.Learn.ReadResult;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ReadResultJobs>("read-result");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var handle = await jobs.EnqueueAsync(new AddNumbers(20, 22));

// learn: 1. Ask whether a result exists yet
// Both reads are point-in-time and never wait. `TryGetResultAsync` says whether a result exists yet.
var early = await handle.TryGetResultAsync<AddNumbersResult>();
Console.WriteLine($"right away:        {(early.Found ? early.Value!.Sum.ToString() : "no result yet")}");

// learn: end

await handle.WaitAsync();

// learn: 2. Read the result you expect
// `GetResultAsync` returns the result, and throws when there is none.
var done = await handle.GetResultAsync<AddNumbersResult>();
Console.WriteLine($"after it finishes: {done.Sum}");

// learn: end

await host.StopAsync();

namespace Acta.Learn.ReadResult
{
    public sealed record AddNumbers(int Left, int Right);

    public sealed record AddNumbersResult(int Sum);

    public static class AddNumbersJob
    {
        [Job("add-numbers")]
        public static async Task<AddNumbersResult> Handle(AddNumbers input, CancellationToken ct)
        {
            await Task.Delay(300, ct);
            return new AddNumbersResult(input.Left + input.Right);
        }
    }
}
```

