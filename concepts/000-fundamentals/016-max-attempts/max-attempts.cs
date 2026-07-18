using Acta;
using Acta.Concepts.MaxAttempts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<MaxAttemptsJobs>("max-attempts");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// The handler always throws; Acta retries up to MaxAttempts, then transitions the job to Failed.
var outcome = await jobs.EnqueueAsync(new FlakyWork("widget"));
await Task.Delay(1500);
var snapshot = await jobs.GetAsync(outcome);
Console.WriteLine($"status={snapshot!.Status} failureCount={snapshot.FailureCount}");

await host.StopAsync();

namespace Acta.Concepts.MaxAttempts
{
    public sealed record FlakyWork(string Item);

    public static class FlakyWorkJob
    {
        // MaxAttempts caps retries; "0s" skips backoff. After the last attempt the job ends Failed.
        [Job("flaky-work", MaxAttempts = 3, Backoff = "0s")]
        public static Task Handle(FlakyWork input) => throw new InvalidOperationException($"Could not process {input.Item}.");
    }
}
