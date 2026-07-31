using Acta;
using Acta.Concepts.StepRetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<StepRetryJobs>("step-retry");
});

// Step fails a few times to show retry; quiet the framework retry logging.
builder.Logging.AddFilter("Acta", LogLevel.Error);

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

await jobs.EnqueueAsync(new FetchForecast("Ljubljana"));
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.StepRetry
{
    public sealed record FetchForecast(string City);

    public sealed class FetchForecastJob
    {
        private static int _attempts;

        [Job("fetch-forecast")]
        public async Task Handle(FetchForecast input, JobContext context, CancellationToken ct)
        {
            // Step has its own retry curve, separate from the job's budget; the failure reaches the job only if the step exhausts its attempts.
            await context.RunStepAsync(
                "call-weather-api",
                async inner =>
                {
                    var attempt = Interlocked.Increment(ref _attempts);
                    Console.WriteLine($"[{input.City}] calling weather API (attempt {attempt})");
                    await Task.Delay(50, inner);
                    if (attempt < 3)
                    {
                        throw new InvalidOperationException("weather API timeout");
                    }
                    Console.WriteLine($"[{input.City}] forecast: 18C, light rain");
                },
                retry => retry.MaxAttempts(3).BackoffInitialDelay(TimeSpan.FromMilliseconds(200)),
                ct
            );

            Console.WriteLine($"[{input.City}] forecast stored");
        }
    }
}
