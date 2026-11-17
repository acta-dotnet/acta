// How to pass typed input to a job
// The input is a record: Acta stores it with the job and hands it to the handler as its first parameter.
using Acta;
using Acta.Learn.JobInput;
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

// learn: 2. Enqueue an instance of it
// The type of the value picks the job: the handler whose first parameter is a `PrintNameTag`.
await jobs.EnqueueAsync(new PrintNameTag("Ada", "workshop"));

// learn: end
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.JobInput
{
    // learn: 1. Write the input as a record
    // The input is plain data. Acta serializes it, stores it with the job, and hands it to the handler.
    public sealed record PrintNameTag(string Name, string Workshop);

    // learn: end

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
