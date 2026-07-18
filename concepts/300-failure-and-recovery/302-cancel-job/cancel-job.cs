using Acta;
using Acta.Concepts.CancelJob;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<CancelJobJobs>("cancel-job");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var outcome = await jobs.EnqueueAsync(new ImportFeed("feed.example.com", SourceGone: true));
await Task.Delay(500);

var snapshot = await jobs.GetAsync(outcome);
Console.WriteLine($"status={snapshot!.Status}");

await host.StopAsync();

namespace Acta.Concepts.CancelJob
{
    public sealed record ImportFeed(string Url, bool SourceGone);

    public sealed class ImportFeedJob
    {
        [Job("import-feed", MaxAttempts = 5)]
        public async Task Handle(ImportFeed feed, JobContext ctx, CancellationToken ct)
        {
            if (feed.SourceGone)
            {
                // CancelAsync ends the job as Cancelled: terminal, not a failure, so no retry and no alert.
                await ctx.CancelAsync("source feed no longer exists", ct);
            }

            Console.WriteLine($"[{feed.Url}] imported");
        }
    }
}
