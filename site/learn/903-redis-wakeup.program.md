# The whole program: wake workers across processes with Redis

The how-to is [/learn/903-redis-wakeup.md](/learn/903-redis-wakeup.md). This file holds every source file of `learn/900-runtime-and-tuning/903-redis-wakeup` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### redis-wakeup.cs

```csharp
// How to wake workers across processes with Redis
// `UseRedisWakeup` announces each enqueue over Redis, so a worker in another process starts the job within
// milliseconds instead of at its next poll.
using Acta;
using Acta.Learn.RedisWakeup;
using Acta.Redis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Two roles ("worker"/"enqueue") so a wake can cross a process boundary via Redis.
var role = args.Length > 0 ? args[0] : "worker";

// No args to the builder: a bare "worker"/"enqueue" would trip the command-line config parser.
var builder = Host.CreateApplicationBuilder();

var redis = builder.Configuration.GetConnectionString("redis") ?? Environment.GetEnvironmentVariable("ACTA_TEST_REDIS") ?? "localhost:6379";

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);

    // learn: Turn the Redis wake-up on in every process
    // Redis pub/sub wake: an enqueue in any process wakes workers in every process. Redis is a
    // latency accelerator, not a correctness dependency; if it's down, workers fall back to the poll floor.
    j.UseRedisWakeup(redis);
    // learn: end

    if (role == "enqueue")
    {
        // Reference gives typed enqueue with no worker loop in this process.
        j.Reference<RedisWakeupJobs>("redis-wakeup");
    }
    else
    {
        j.Run<RedisWakeupJobs>("redis-wakeup");

        // Poll once a minute on purpose: a millisecond start then proves it was the Redis wake, not the poll.
        j.ConfigureOptions(o => o.SafetyPollInterval = TimeSpan.FromSeconds(60));
    }
});

using var host = builder.Build();

if (role == "enqueue")
{
    await host.StartAsync();
    var jobs = host.Services.GetRequiredService<IJobs>();
    await jobs.EnqueueAsync(new Ping(DateTime.Now.ToString("HH:mm:ss.fff")));
    Console.WriteLine("Enqueued a ping. A worker in another process should print it within milliseconds.");
    await host.StopAsync();
    return;
}

Console.WriteLine("Worker running (it polls only every 60s). In another terminal, enqueue a ping:");
Console.WriteLine("  dotnet run --project learn/900-runtime-and-tuning/903-redis-wakeup -- enqueue");
Console.WriteLine("It shows up here almost instantly - that is the Redis wake, not the poll. Ctrl+C to stop.");

await host.StartAsync();
await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.RedisWakeup
{
    public sealed record Ping(string EnqueuedAt);

    public static class PingJob
    {
        [Job("ping")]
        public static void Handle(Ping input) =>
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} woke and ran a ping enqueued at {input.EnqueuedAt}");
    }
}
```

