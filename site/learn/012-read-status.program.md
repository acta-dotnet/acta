# The whole program: read a job's status

The how-to is [/learn/012-read-status.md](/learn/012-read-status.md). This file holds every source file of `concepts/000-fundamentals/012-read-status` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### read-status.cs

```csharp
// How to read a job's status
// `GetAsync` answers a snapshot of the job: its ref, its name and namespace, its status, and when it was created.
using Acta;
using Acta.Concepts.ReadStatus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ReadStatusJobs>("read-status");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var handle = await jobs.EnqueueAsync(new SendWelcomeEmail("sam@example.com", "Sam"));
await handle.WaitAsync();

// learn: Read the job with GetAsync
// `GetAsync` returns a snapshot of the job's current state, or null when there is no such job. The handle that
// `EnqueueAsync` returned names the job; so does its `JobRef`.
var snapshot = await jobs.GetAsync(handle);
Console.WriteLine($"job={snapshot!.JobRef} name={snapshot.JobName} ns={snapshot.JobNamespace}");
Console.WriteLine($"status={snapshot.Status} created={snapshot.CreatedAtUtc:O}");

// learn: end

await host.StopAsync();

namespace Acta.Concepts.ReadStatus
{
    public sealed record SendWelcomeEmail(string Email, string Name);

    public static class WelcomeJob
    {
        [Job("send-welcome-email")]
        public static void Handle(SendWelcomeEmail input) => Console.WriteLine($"Sent welcome email to {input.Name} <{input.Email}>");
    }
}
```

