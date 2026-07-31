using Acta;
using Acta.Concepts.FailJob;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<FailJobJobs>("fail-job");
});

// Quiet the framework's failure logging; the job fails on purpose.
builder.Logging.AddFilter("Acta", LogLevel.Error);

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var outcome = await jobs.EnqueueAsync(new ProcessUpload("photo.jpg", Corrupt: true));
await Task.Delay(500);

var snapshot = await jobs.GetAsync(outcome);
Console.WriteLine($"status={snapshot!.Status}, failureCount={snapshot.FailureCount}");

await host.StopAsync();

namespace Acta.Concepts.FailJob
{
    public sealed record ProcessUpload(string FileName, bool Corrupt);

    public sealed class ProcessUploadJob
    {
        // FailAsync is terminal even with MaxAttempts = 5: it ends the job, it does not retry.
        [Job("process-upload", MaxAttempts = 5)]
        public async Task Handle(ProcessUpload upload, JobContext context, CancellationToken ct)
        {
            if (upload.Corrupt)
            {
                // FailAsync ends the job as Failed now (no retry); a thrown exception retries to MaxAttempts.
                await context.FailAsync("file is corrupt and cannot be processed", ct);
            }

            Console.WriteLine($"[{upload.FileName}] file processed");
        }
    }
}
