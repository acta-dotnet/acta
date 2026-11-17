// How to report how far along a job is
// `SetProgressAsync` stores how far the job has come as the checkpoint `sys.progress`, which the dashboard lists
// while the job runs.
using Acta;
using Acta.Learn.Progress;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ProgressJobs>("progress");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

await jobs.EnqueueAsync(new ImportFile("customers.csv", 20));
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.Progress
{
    public sealed record ImportFile(string Name, int Rows);

    public sealed class ImportFileJob
    {
        [Job("import-file")]
        public async Task Handle(ImportFile input, JobContext context, CancellationToken ct)
        {
            for (var row = 1; row <= input.Rows; row++)
            {
                await Task.Delay(100, ct);

                var percent = row * 100 / input.Rows;

                // learn: Store the progress as the work advances
                // Each call replaces the last value. The dashboard shows it with the job's checkpoints; a page of your
                // own reads a variable instead (415).
                await context.SetProgressAsync(percent, ct);
                // learn: end

                var filled = percent / 10;
                Console.Write($"\r[{input.Name}] [{new string('#', filled)}{new string('-', 10 - filled)}] {percent, 3}%");
            }

            Console.WriteLine();
            Console.WriteLine($"[{input.Name}] import complete");
        }
    }
}
