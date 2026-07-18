using Acta;
using Acta.Concepts.JobInput;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<JobInputJobs>("job-input");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// The job takes a typed record as its input.
await jobs.EnqueueAsync(new PrintNameTag("Ada", "workshop"));
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.JobInput
{
    public sealed record PrintNameTag(string Name, string Workshop);

    public static class PrintNameTagJob
    {
        [Job("print-name-tag")]
        public static async Task Handle(PrintNameTag tag, CancellationToken ct)
        {
            await Task.Delay(50, ct);
            Console.WriteLine($"Printed name tag: {tag.Name} ({tag.Workshop})");
        }
    }
}
