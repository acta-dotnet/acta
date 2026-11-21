# The whole program: enqueue by the job's generated call

The how-to is [/learn/023-job-call.md](/learn/023-job-call.md). This file holds every source file of `learn/000-fundamentals/023-job-call` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### job-call.cs

```csharp
// How to enqueue by the job's generated call
// The manifest's generated call names the job and takes its handler's parameters, several of them as one input.
using Acta;
using Acta.Learn.JobCall;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<JobCallJobs>("job-call");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// learn: 1. Enqueue through the generated call
// The manifest's generated call names the job and takes its handler's parameters; the namespace resolves from the
// registered manifest, and nothing is inferred from the input's type.
var single = await jobs.EnqueueAsync(JobCallJobs.Hello(new Hello("World")));
var several = await jobs.EnqueueAsync(JobCallJobs.Greet("Ada", 3), o => o.IdempotencyKey("greet-ada"));

// learn: end
Console.WriteLine($"Enqueued hello as {single.JobRef} and greet as {several.JobRef}.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.JobCall
{
    public sealed record Hello(string Name);

    public static class HelloJobs
    {
        [Job("hello")]
        public static void Handle(Hello input) => Console.WriteLine($"Hello, {input.Name}!");

        // learn: 2. Give a handler several parameters
        // Several input parameters travel as one generated record, JobCallJobs.GreetArgs, stored as {"name":..,"times":..}.
        [Job("greet")]
        public static void Greet(string name, int times)
        {
            for (var i = 0; i < times; i++)
            {
                Console.WriteLine($"Greetings, {name}!");
            }
        }
        // learn: end
    }
}
```

