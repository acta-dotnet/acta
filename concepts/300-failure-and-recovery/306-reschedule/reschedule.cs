using Acta;
using Acta.Concepts.Reschedule;
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

namespace Acta.Concepts.Reschedule
{
    public sealed record PollResource(string Url);

    public sealed class PollResourceJob
    {
        private static int _tries;

        [Job("poll-resource")]
        public async Task Handle(PollResource input, JobContext ctx, CancellationToken ct)
        {
            var ready = Interlocked.Increment(ref _tries) >= 2;
            if (!ready)
            {
                // RescheduleAsync re-arms the job after a delay without burning the retry budget; the
                // handler stops here and replays on the next run (RescheduleUntilAsync takes an absolute time).
                Console.WriteLine($"[{input.Url}] not ready, rescheduling...");
                await ctx.RescheduleAsync(TimeSpan.FromSeconds(1), "resource not ready", ct);
            }

            Console.WriteLine($"[{input.Url}] ready - processed");
        }
    }
}
