// How to stop cleanly when a job is cancelled
// A handler takes a `CancellationToken` and passes it to everything it awaits, so a shutdown or a cancel ends it
// promptly.
using Acta;
using Acta.Concepts.Cancellation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<CancellationJobs>("cancellation-token");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

await jobs.EnqueueAsync(new SlowImport(3));
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.Cancellation
{
    public sealed record SlowImport(int Batches);

    public static class ImportJob
    {
        // Acta trips the CancellationToken on shutdown or lease loss; the handler must observe it.
        [Job("slow-import")]
        public static async Task Handle(SlowImport input, CancellationToken ct)
        {
            for (var batch = 1; batch <= input.Batches; batch++)
            {
                // learn: Check the token between pieces of work, and pass it to what you await
                // A handler that ignores its token keeps running after the worker has been told to stop.
                ct.ThrowIfCancellationRequested();
                await Task.Delay(100, ct);
                // learn: end
                Console.WriteLine($"Imported batch {batch}/{input.Batches}");
            }
        }
    }
}
