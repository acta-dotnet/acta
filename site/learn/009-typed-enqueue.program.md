# The whole program: enqueue by the input's type

The how-to is [/learn/009-typed-enqueue.md](/learn/009-typed-enqueue.md). This file holds every source file of `learn/000-fundamentals/009-typed-enqueue` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### typed-enqueue.cs

```csharp
// How to enqueue by the input's type
// `EnqueueAsync` takes the input alone: its type names the job, so there is no name string to get wrong.
using Acta;
using Acta.Learn.TypedEnqueue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<TypedEnqueueJobs>("typed-enqueue");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// learn: Enqueue the input and let its type pick the job
// No job-name string: Acta picks the job from the input type. The handle that comes back carries the job's ref and
// says whether this call inserted the job.
var handle = await jobs.EnqueueAsync(new GreetUser("Sam"));

// learn: end
Console.WriteLine($"Enqueued job {handle.JobRef} ({handle.Action}). Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.TypedEnqueue
{
    public sealed record GreetUser(string Name);

    public static class GreetUserJob
    {
        [Job("greet-user")]
        public static void Handle(GreetUser input) => Console.WriteLine($"Hi, {input.Name}!");
    }
}
```

