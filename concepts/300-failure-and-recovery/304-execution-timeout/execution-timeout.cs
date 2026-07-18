using Acta;
using Acta.Concepts.ExecutionTimeout;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ExecutionTimeoutJobs>("execution-timeout");
});

// Quiet the framework's failure logging; the job times out on purpose.
builder.Logging.AddFilter("Acta", LogLevel.Error);

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var outcome = await jobs.EnqueueAsync(new SlowReport("q3"));
await Task.Delay(4000); // longer than the 2s execution timeout

var snapshot = await jobs.GetAsync(outcome);
Console.WriteLine($"status={snapshot!.Status}");

await host.StopAsync();

namespace Acta.Concepts.ExecutionTimeout
{
    public sealed record SlowReport(string Name);

    public sealed class SlowReportJob
    {
        // ExecutionTimeout caps a single attempt: after 2s the framework trips the CancellationToken
        // and the attempt ends as a timeout.
        [Job("slow-report", ExecutionTimeout = "2s", MaxAttempts = 1)]
        public async Task Handle(SlowReport input, JobContext ctx, CancellationToken ct)
        {
            Console.WriteLine($"[{input.Name}] generating report (this runs too long)...");
            await Task.Delay(TimeSpan.FromSeconds(10), ct); // exceeds the 2s timeout, so ct trips here
            Console.WriteLine($"[{input.Name}] report done"); // never reached
        }
    }
}
