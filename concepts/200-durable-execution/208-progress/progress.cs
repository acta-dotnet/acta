using Acta;
using Acta.Concepts.Progress;
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

namespace Acta.Concepts.Progress
{
    public sealed record ImportFile(string Name, int Rows);

    public sealed class ImportFileJob
    {
        [Job("import-file")]
        public async Task Handle(ImportFile input, JobContext ctx, CancellationToken ct)
        {
            for (var row = 1; row <= input.Rows; row++)
            {
                await Task.Delay(100, ct);

                var percent = row * 100 / input.Rows;

                // Writes durable state read out-of-band (the __progress variable), not by the caller.
                await ctx.SetProgressAsync(percent, ct);

                var filled = percent / 10;
                Console.Write($"\r[{input.Name}] [{new string('#', filled)}{new string('-', 10 - filled)}] {percent, 3}%");
            }

            Console.WriteLine();
            Console.WriteLine($"[{input.Name}] import complete");
        }
    }
}
