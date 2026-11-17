# The whole program: cut off an attempt that runs too long

The how-to is [/learn/304-execution-timeout.md](/learn/304-execution-timeout.md). This file holds every source file of `concepts/300-failure-and-recovery/304-execution-timeout` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### execution-timeout.cs

```csharp
// How to cut off an attempt that runs too long
// `ExecutionTimeout` caps one attempt: when the time is up, the handler's `CancellationToken` trips and the attempt
// ends as a timeout.
using Acta;
using Acta.Concepts.ExecutionTimeout;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ExecutionTimeoutJobs>("execution-timeout");
});

// Quiet the framework's failure logging; the job times out on purpose.
builder.Logging.AddFilter("Acta", LogLevel.Error);

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var outcome = await jobs.EnqueueAsync(new SlowReport("q3"));
await Task.Delay(4000); // longer than the 2s execution timeout

var snapshot = await jobs.GetAsync(outcome);
Console.WriteLine($"status={snapshot!.Status}");

await host.StopAsync();

namespace Acta.Concepts.ExecutionTimeout
{
    public sealed record SlowReport(string Name);

    public sealed class SlowReportJob
    {
        // learn: 1. Set the timeout on the job
        // ExecutionTimeout caps a single attempt: after 2s the framework trips the CancellationToken
        // and the attempt ends as a timeout.
        [Job("slow-report", ExecutionTimeout = "2s", MaxAttempts = 1)]
        // learn: end
        public async Task Handle(SlowReport input, JobContext context, CancellationToken ct)
        {
            Console.WriteLine($"[{input.Name}] generating report (this runs too long)...");
            // learn: 2. Pass the token to the work, so the timeout can stop it
            await Task.Delay(TimeSpan.FromSeconds(10), ct); // exceeds the 2s timeout, so ct trips here
            // learn: end
            Console.WriteLine($"[{input.Name}] report done"); // never reached
        }
    }
}
```

