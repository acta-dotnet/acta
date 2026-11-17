// How to choose between starting a job and waiting for it
// `EnqueueAsync` returns as soon as the job is stored; `CallAsync` enqueues, waits, and returns the job's outcome.
using Acta;
using Acta.Learn.CallVsEnqueue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<CallVsEnqueueJobs>("call-vs-enqueue");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// learn: 1. Start a job and carry on
// EnqueueAsync returns immediately, before the job runs.
var enqueued = await jobs.EnqueueAsync(new AddNumbers(1, 1));

// learn: end
Console.WriteLine($"EnqueueAsync returned right away: job {enqueued.JobRef}, status {(await enqueued.GetStatusAsync())?.Status}");

// learn: 2. Start a job and wait for its result
// CallAsync enqueues, then waits for the job and returns its outcome: `ValueOrThrow()` gives the result, or throws
// when the job failed or the wait timed out. It holds its caller that long, and a timeout stops only the wait;
// inside a handler, call `ctx.CallChildAsync` instead (213).
var executed = await jobs.CallAsync(CallVsEnqueueJobs.AddNumbers(new AddNumbers(2, 2)));

// learn: end
Console.WriteLine($"CallAsync waited and returned: job {executed.JobId}, result {executed.ValueOrThrow().Sum}");

await host.StopAsync();

namespace Acta.Learn.CallVsEnqueue
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
