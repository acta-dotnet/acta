# The whole program: keep several jobs in one class

The how-to is [/learn/006-many-jobs-one-class.md](/learn/006-many-jobs-one-class.md). This file holds every source file of `concepts/000-fundamentals/006-many-jobs-one-class` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### many-jobs-one-class.cs

```csharp
// How to keep several jobs in one class
// A class can hold several `[Job]` methods. Each is a job of its own, with its own generated call.
using Acta;
using Acta.Concepts.ManyJobsOneClass;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ManyJobsOneClassJobs>("many-jobs-one-class");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// learn: 2. Enqueue each job by its own call
// Each [Job] method gets its own generated call, named after the job.
await jobs.EnqueueAsync(ManyJobsOneClassJobs.Shout("acta"));
await jobs.EnqueueAsync(ManyJobsOneClassJobs.DoubleIt(21));
await jobs.EnqueueAsync(ManyJobsOneClassJobs.Halve(9.0));

// learn: end
Console.WriteLine("Enqueued 3 jobs. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.ManyJobsOneClass
{
    public static class TextAndMath
    {
        // learn: 1. Put the jobs in one class
        // Each [Job] method in a class registers independently, under the name in its attribute.
        [Job("shout")]
        public static void Shout(string text) => Console.WriteLine(text.ToUpperInvariant());

        [Job("double-it")]
        public static void Double(int n) => Console.WriteLine(n * 2);

        [Job("halve")]
        public static void Halve(double x) => Console.WriteLine(x / 2);
        // learn: end
    }
}
```

