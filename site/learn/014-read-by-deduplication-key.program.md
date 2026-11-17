# The whole program: find a job by its deduplication key

The how-to is [/learn/014-read-by-deduplication-key.md](/learn/014-read-by-deduplication-key.md). This file holds every source file of `learn/000-fundamentals/014-read-by-deduplication-key` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### read-by-deduplication-key.cs

```csharp
// How to find a job by its deduplication key
// `JobLookup.ByDeduplicationKey` finds a job by the key it was enqueued with, when you did not keep its ref.
using Acta;
using Acta.Learn.ReadByDeduplicationKey;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ReadByDeduplicationKeyJobs>("read-by-deduplication-key");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// learn: 1. Enqueue with a key you can build again later
// Typed options accept an exact final key; compose the definition-qualified key explicitly.
var businessKey = "welcome:sam";
var finalKey = DeduplicationKey.ForDefinition("send-welcome-email", businessKey);
await jobs.EnqueueAsync(new SendWelcomeEmail("sam@example.com", "Sam"), o => o.DeduplicationKey(finalKey));

// learn: end

// learn: 2. Look the job up by that key
// Lookup uses the exact stored final key; it does not compose a definition name from a business key.
var snapshot = await jobs.GetAsync(JobLookup.ByDeduplicationKey("read-by-deduplication-key", finalKey));

// learn: end
Console.WriteLine($"Found job {snapshot!.JobRef} by deduplication key '{snapshot.DeduplicationKey}', status {snapshot.Status}.");
Console.WriteLine("Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.ReadByDeduplicationKey
{
    public sealed record SendWelcomeEmail(string Email, string Name);

    public static class WelcomeJob
    {
        [Job("send-welcome-email")]
        public static void Handle(SendWelcomeEmail input) => Console.WriteLine($"Sent welcome email to {input.Name} <{input.Email}>");
    }
}
```

