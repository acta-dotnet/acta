// How to return a result and wait for it
// What the handler returns is stored with the job as its result. `WaitAsync<T>` follows the job to its end and hands
// the result back, typed.
using Acta;
using Acta.Concepts.JobResult;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<JobResultJobs>("job-result");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var handle = await jobs.EnqueueAsync(new AddNumbers(2, 3));

// learn: 2. Wait for the job and read its result
// WaitAsync follows the job to its terminal state and carries the typed result.
var result = (await handle.WaitAsync<AddNumbersResult>()).ValueOrThrow();
Console.WriteLine($"2 + 3 = {result.Sum}");

// learn: end

await host.StopAsync();

namespace Acta.Concepts.JobResult
{
    public sealed record AddNumbers(int Left, int Right);

    public sealed record AddNumbersResult(int Sum);

    public static class AddNumbersJob
    {
        // learn: 1. Return the result from the handler
        // What the handler returns is stored with the job as its result.
        [Job("add-numbers")]
        public static AddNumbersResult Handle(AddNumbers input) => new(input.Left + input.Right);
        // learn: end
    }
}
