using Acta;
using Acta.Concepts.ExecuteVsEnqueue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ExecuteVsEnqueueJobs>("execute-vs-enqueue");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// EnqueueAsync returns immediately, before the job runs.
var enqueued = await jobs.EnqueueAsync(new AddNumbers(1, 1));
Console.WriteLine($"EnqueueAsync returned right away: job {enqueued.JobRef}, status {await jobs.GetStatusAsync(enqueued)}");

// ExecuteAndWaitAsync enqueues, then waits and returns the result. (See 013-read-result for the read-back path.)
var executed = await jobs.ExecuteAndWaitAsync<AddNumbers, AddNumbersResult>(new AddNumbers(2, 2));
Console.WriteLine($"ExecuteAndWaitAsync waited and returned: job {executed.JobId}, result {executed.ValueOrThrow().Sum}");

await host.StopAsync();

namespace Acta.Concepts.ExecuteVsEnqueue
{
    public sealed record AddNumbers(int Left, int Right);

    public sealed record AddNumbersResult(int Sum);

    public static class AddNumbersJob
    {
        [Job("add-numbers")]
        public static async Task<AddNumbersResult> Handle(AddNumbers input, CancellationToken ct)
        {
            await Task.Delay(500, ct);
            return new AddNumbersResult(input.Left + input.Right);
        }
    }
}
