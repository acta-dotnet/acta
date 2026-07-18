using Acta;
using Acta.Concepts.RedisWakeup;
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

    // Redis pub/sub wake: an enqueue in any process wakes workers in every process. Redis is a
    // latency accelerator, not a correctness dependency; if it's down, workers fall back to the poll floor.
    j.UseRedisWakeup(redis);

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
Console.WriteLine("  dotnet run --project concepts/900-runtime-and-tuning/903-redis-wakeup -- enqueue");
Console.WriteLine("It shows up here almost instantly - that is the Redis wake, not the poll. Ctrl+C to stop.");

await host.StartAsync();
await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.RedisWakeup
{
    public sealed record Ping(string EnqueuedAt);

    public static class PingJob
    {
        [Job("ping")]
        public static void Handle(Ping input) =>
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} woke and ran a ping enqueued at {input.EnqueuedAt}");
    }
}
