# The whole program: fail a job for good, with no retry

The how-to is [/learn/301-fail-job.md](/learn/301-fail-job.md). This file holds every source file of `concepts/300-failure-and-recovery/301-fail-job` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### fail-job.cs

```csharp
// How to fail a job for good, with no retry
// `FailAsync` ends the job as `Failed` at once, whatever its retry budget. A thrown exception would be retried.
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
                // learn: End the job as failed where retrying cannot help
                // FailAsync ends the job as Failed now (no retry); a thrown exception retries to MaxAttempts.
                await context.FailAsync("file is corrupt and cannot be processed", ct);
                // learn: end
            }

            Console.WriteLine($"[{upload.FileName}] file processed");
        }
    }
}
```

