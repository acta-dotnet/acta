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

var outcome = await jobs.EnqueueAsync(new AddNumbers(2, 3));
await Task.Delay(500);
var result = await jobs.GetResultAsync<AddNumbersResult>(outcome);
Console.WriteLine($"2 + 3 = {result!.Sum}");

await host.StopAsync();

namespace Acta.Concepts.JobResult
{
    public sealed record AddNumbers(int Left, int Right);

    public sealed record AddNumbersResult(int Sum);

    public static class AddNumbersJob
    {
        [Job("add-numbers")]
        public static AddNumbersResult Handle(AddNumbers input) => new(input.Left + input.Right);
    }
}
