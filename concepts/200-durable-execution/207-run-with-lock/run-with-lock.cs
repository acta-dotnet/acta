using Acta;
using Acta.Concepts.RunWithLock;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<RunWithLockJobs>("run-with-lock");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// Both jobs share a lock key, so they take turns.
await jobs.EnqueueAsync(new UpdateInventory("sku-A"));
await jobs.EnqueueAsync(new UpdateInventory("sku-B"));
Console.WriteLine("Enqueued 2 jobs. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.RunWithLock
{
    public sealed record UpdateInventory(string Sku);

    public sealed class UpdateInventoryJob
    {
        [Job("update-inventory")]
        public async Task Handle(UpdateInventory input, JobContext ctx, CancellationToken ct)
        {
            // Named lock held for the action; same key serializes both concurrently-running jobs. To exclude the whole job instead, set ExclusiveKey on enqueue (see 209-exclusive-key).
            await ctx.RunWithLockAsync(
                "inventory",
                async () =>
                {
                    Console.WriteLine($"[{input.Sku}] acquired lock, updating inventory...");
                    await Task.Delay(1000, ct);
                    Console.WriteLine($"[{input.Sku}] done, releasing lock");
                },
                ct: ct
            );
        }
    }
}
