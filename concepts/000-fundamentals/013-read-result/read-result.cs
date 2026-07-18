using Acta;
using Acta.Concepts.ReadResult;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ReadResultJobs>("read-result");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// GetResultAsync is a point-in-time read that never waits; no result until the job is Done.
var outcome = await jobs.EnqueueAsync(new AddNumbers(20, 22));
var early = await jobs.GetResultAsync<AddNumbersResult>(outcome);
Console.WriteLine($"right away:        {(early is null ? "no result yet" : early.Sum.ToString())}");

await Task.Delay(500);
var done = await jobs.GetResultAsync<AddNumbersResult>(outcome);
Console.WriteLine($"after it finishes: {done!.Sum}");

await host.StopAsync();

namespace Acta.Concepts.ReadResult
{
    public sealed record AddNumbers(int Left, int Right);

    public sealed record AddNumbersResult(int Sum);

    public static class AddNumbersJob
    {
        [Job("add-numbers")]
        public static async Task<AddNumbersResult> Handle(AddNumbers input, CancellationToken ct)
        {
            await Task.Delay(300, ct);
            return new AddNumbersResult(input.Left + input.Right);
        }
    }
}
