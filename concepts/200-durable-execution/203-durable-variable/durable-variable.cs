using Acta;
using Acta.Concepts.DurableVariable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<DurableVariableJobs>("durable-variable");
});

// Handler fails once to show retry; quiet the framework failure warning/alert.
builder.Logging.AddFilter("Acta", LogLevel.Error);

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

await jobs.EnqueueAsync(new SyncUsers("crm"));
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.DurableVariable
{
    public sealed record SyncUsers(string Source);

    public sealed class SyncUsersJob
    {
        private static int _attempts;

        [Job("sync-users", MaxAttempts = 2, Backoff = "2s")]
        public async Task Handle(SyncUsers input, JobContext ctx, CancellationToken ct)
        {
            // Pin the sync window once: GetOrSet stores it on the first pass and replays it on the retry, so the window can't shift between attempts.
            var syncedAt = await ctx.GetOrSetVariableAsync("synced-at-utc", () => DateTime.UtcNow, ct);
            Console.WriteLine($"[{input.Source}] syncing changes as of {syncedAt:HH:mm:ss.fff} UTC");

            // Fail the first attempt; the same timestamp comes back on the retry.
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                Console.WriteLine($"[{input.Source}] transient failure - retrying...");
                throw new InvalidOperationException("simulated transient failure");
            }

            Console.WriteLine($"[{input.Source}] sync complete (window pinned at {syncedAt:HH:mm:ss.fff} UTC)");
        }
    }
}
