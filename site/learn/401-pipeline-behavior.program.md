# The whole program: wrap every handler in your own middleware

The how-to is [/learn/401-pipeline-behavior.md](/learn/401-pipeline-behavior.md). This file holds every source file of `learn/400-observability-and-alerts/401-pipeline-behavior` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### pipeline-behavior.cs

```csharp
// How to wrap every handler in your own middleware
// An `IJobPipelineBehavior` wraps every handler call, the way middleware wraps a request.
using System.Diagnostics;
using Acta;
using Acta.Learn.PipelineBehavior;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    // learn: 2. Register the behavior
    // The first one registered is the outermost.
    j.AddPipelineBehavior<TimingBehavior>();
    // learn: end
    j.Run<PipelineBehaviorJobs>("pipeline-behavior");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

await jobs.EnqueueAsync(new GreetUser("Sam"));
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.PipelineBehavior
{
    public sealed record GreetUser(string Name);

    public static class GreetUserJob
    {
        [Job("greet-user")]
        public static async Task Handle(GreetUser input, CancellationToken ct)
        {
            await Task.Delay(100, ct);
            Console.WriteLine($"  Hi, {input.Name}!");
        }
    }

    // Wraps every handler invocation; first registered is outermost. Resolved per attempt, so it can
    // take constructor dependencies (including the scoped JobContext).
    public sealed class TimingBehavior : IJobPipelineBehavior
    {
        public async ValueTask<JobHandlerOutput> InvokeAsync(
            object request,
            JobContext context,
            JobBehaviorDelegate next,
            CancellationToken ct
        )
        {
            // learn: 1. Write the behavior around the call to the handler
            // What stands before `next()` runs before the handler, and what stands after it runs after.
            var sw = Stopwatch.StartNew();
            Console.WriteLine($"-> {context.JobName} #{context.JobId} starting");
            var result = await next();
            Console.WriteLine($"<- {context.JobName} #{context.JobId} done in {sw.ElapsedMilliseconds} ms");
            return result;
            // learn: end
        }
    }
}
```

