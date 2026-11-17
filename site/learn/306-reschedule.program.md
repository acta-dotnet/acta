# The whole program: check again later without spending a retry

The how-to is [/learn/306-reschedule.md](/learn/306-reschedule.md). This file holds every source file of `learn/300-failure-and-recovery/306-reschedule` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### reschedule.cs

```csharp
// How to check again later without spending a retry
// `RescheduleAsync` ends the execution and runs the handler again after a delay, without counting as a failure or
// spending a retry.
using Acta;
using Acta.Learn.Reschedule;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<RescheduleJobs>("reschedule");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

await jobs.EnqueueAsync(new PollResource("api.example.com/report"));
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.Reschedule
{
    public sealed record PollResource(string Url);

    public sealed class PollResourceJob
    {
        private static int _tries;

        [Job("poll-resource")]
        public async Task Handle(PollResource input, JobContext context, CancellationToken ct)
        {
            var ready = Interlocked.Increment(ref _tries) >= 2;
            if (!ready)
            {
                // learn: Reschedule when the answer is not there yet
                // RescheduleAsync re-arms the job after a delay without burning the retry budget; the
                // handler stops here and replays on the next run (RescheduleUntilAsync takes an absolute time).
                Console.WriteLine($"[{input.Url}] not ready, rescheduling...");
                await context.RescheduleAsync(TimeSpan.FromSeconds(1), "resource not ready", ct);
                // learn: end
            }

            Console.WriteLine($"[{input.Url}] ready - processed");
        }
    }
}
```

