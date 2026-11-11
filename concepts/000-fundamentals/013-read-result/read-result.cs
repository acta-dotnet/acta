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

// Both reads are point-in-time and never wait. TryGetResultAsync says whether a result exists yet;
// GetResultAsync returns the result you expect, and throws when there is none.
var handle = await jobs.EnqueueAsync(new AddNumbers(20, 22));
var early = await handle.TryGetResultAsync<AddNumbersResult>();
Console.WriteLine($"right away:        {(early.Found ? early.Value!.Sum.ToString() : "no result yet")}");

await handle.WaitAsync();
var done = await handle.GetResultAsync<AddNumbersResult>();
Console.WriteLine($"after it finishes: {done.Sum}");

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
