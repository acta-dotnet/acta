# The whole program: pass one plain value as the input

The how-to is [/learn/005-scalar-input.md](/learn/005-scalar-input.md). This file holds every source file of `learn/000-fundamentals/005-scalar-input` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### scalar-input.cs

```csharp
// How to pass one plain value as the input
// The input can be one plain value, such as a string or a number, with no record around it.
using Acta;
using Acta.Learn.ScalarInput;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ScalarInputJobs>("scalar-input");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// learn: 2. Enqueue it by its generated call
// The generated call names the job, and a plain scalar works as its input; no record needed.
await jobs.EnqueueAsync(ScalarInputJobs.Shout("durable jobs are simple"));

// learn: end
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.ScalarInput
{
    public static class ShoutJob
    {
        // learn: 1. Take the plain value as the handler's parameter
        [Job("shout")]
        public static void Handle(string message) => Console.WriteLine(message.ToUpperInvariant());
        // learn: end
    }
}
```

