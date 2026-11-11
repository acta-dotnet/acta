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

// The steps fail on purpose to show the retries; quiet the framework retry logging.
builder.Logging.AddFilter("Acta", LogLevel.Error);

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// Ljubljana's forecast arrives on the third attempt; Atlantis's never does, so its last attempt compensates.
await jobs.EnqueueAsync(new FetchForecast("Ljubljana"));
await jobs.EnqueueAsync(new FetchForecast("Atlantis"));
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.StepRetry
{
    public sealed record FetchForecast(string City);

    public sealed class FetchForecastJob
    {
        // A step retries through its job: a body that throws fails the attempt, MaxAttempts and Backoff retry it,
        // and the retry replays the steps that already succeeded, so the slot is reserved once.
        [Job("fetch-forecast", MaxAttempts = 3, Backoff = "1s")]
        public async Task Handle(FetchForecast input, JobContext context, CancellationToken ct)
        {
            await context.RunStepAsync(
                "reserve-slot",
                async inner =>
                {
                    Console.WriteLine($"[{input.City}] report slot reserved");
                    await Task.CompletedTask;
                },
                ct
            );

            try
            {
                await context.RunStepAsync(
                    "call-weather-api",
                    async inner =>
                    {
                        Console.WriteLine($"[{input.City}] calling weather API (attempt {context.ExecutionNumber})");
                        await Task.Delay(50, inner);
                        if (input.City == "Atlantis" || context.ExecutionNumber < 3)
                        {
                            throw new InvalidOperationException("weather API timeout");
                        }
                        Console.WriteLine($"[{input.City}] forecast: 18C, light rain");
                    },
                    ct
                );
            }
            // A StepFailedException is not retried, so it ends the job on any attempt while IsLastAttempt reads false.
            catch (Exception ex) when (context.IsLastAttempt || ex is StepFailedException)
            {
                // This failure ends the job, so give the slot back first. Best effort: a crash on the last attempt
                // skips this block, which is why the release is a step of its own and safe to repeat.
                await context.RunStepAsync(
                    "release-slot",
                    async inner =>
                    {
                        Console.WriteLine($"[{input.City}] report slot released; the job fails for good");
                        await Task.CompletedTask;
                    },
                    ct
                );
                throw;
            }

            Console.WriteLine($"[{input.City}] forecast stored");
        }
    }
}
