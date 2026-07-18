// Concept: Backoff shapes the exponential retry curve; NextRunAtUtc grows with each failure.
using Acta;
using Acta.Concepts.BackoffCurve;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<BackoffCurveJobs>("backoff-curve");
});

// Quiet expected-failure noise; the handler always throws on purpose.
builder.Logging.AddFilter("Acta", LogLevel.Error);

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

Console.WriteLine("Enqueueing a flaky job - it always throws to exhaust MaxAttempts=4.");
Console.WriteLine(
    "Curve: initial=1s, multiplier=2, max=8s, jitter=0 -> 3 retry gaps ~1s, 2s, 4s after MaxAttempts=4 is exhausted, then Failed."
);

var outcome = await jobs.EnqueueAsync(new FlakyTransfer("txn-1"));

// Poll until the job reaches a terminal status, printing NextRunAtUtc after each rearm.
short lastFailureCount = 0;
while (true)
{
    await Task.Delay(200);
    var snapshot = await jobs.GetAsync(outcome);
    if (snapshot is null)
        break;

    if (snapshot.FailureCount != lastFailureCount && snapshot.NextRunAtUtc.HasValue)
    {
        var gap = snapshot.NextRunAtUtc.Value - DateTime.UtcNow;
        Console.WriteLine(
            $"failure #{snapshot.FailureCount}: next retry at {snapshot.NextRunAtUtc.Value:HH:mm:ss.fff} UTC (in ~{gap.TotalSeconds:F1}s)"
        );
        lastFailureCount = snapshot.FailureCount;
    }

    if (snapshot.Status is JobStatusCode.Failed or JobStatusCode.Done or JobStatusCode.Cancelled)
    {
        Console.WriteLine($"job ended: status={snapshot.Status} failureCount={snapshot.FailureCount}");
        break;
    }
}

await host.StopAsync();

namespace Acta.Concepts.BackoffCurve
{
    public sealed record FlakyTransfer(string Id);

    public static class FlakyTransferJob
    {
        // Exponential backoff: delays double per failure (1s, 2s, 4s) up to MaxAttempts=4.
        // exact makes the curve deterministic so NextRunAtUtc gaps are observable.
        [Job("flaky-transfer", MaxAttempts = 4, Backoff = "1s..8s x2 exact")]
        public static Task Handle(FlakyTransfer input) =>
            throw new InvalidOperationException($"Transfer {input.Id} failed: upstream unavailable.");
    }
}
