# The whole program: keep a repeated request from creating a second job

The how-to is [/learn/010-deduplication-key.md](/learn/010-deduplication-key.md). This file holds every source file of `concepts/000-fundamentals/010-deduplication-key` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### deduplication-key.cs

```csharp
// How to keep a repeated request from creating a second job
// A deduplication key makes an enqueue idempotent: a second enqueue with the same key answers the job that is
// already there.
using Acta;
using Acta.Concepts.DeduplicationKey;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<DeduplicationKeyJobs>("deduplication-key");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var businessKey = $"welcome:{Guid.NewGuid():N}";
var finalKey = DeduplicationKey.ForDefinition("send-welcome-email", businessKey);

// learn: 1. Give the enqueue a key that names the request
// The raw builder knows the definition name, so Deduplicate(businessKey) is the primary API there.
var request = JobRequestBuilder
    .Create("deduplication-key", "send-welcome-email")
    .Json(new SendWelcomeEmail("sam@example.com", "Sam"))
    .Deduplicate(businessKey)
    .Build();

var first = await jobs.EnqueueAsync(request, CancellationToken.None);

// learn: end

// learn: 2. Repeat the enqueue and get the same job back
// Typed options do not know the routed definition yet, so they accept the exact final key. The
// equivalent composed key converges on the same job, and this second enqueue is deduplicated.
var second = await jobs.EnqueueAsync(new SendWelcomeEmail("sam@example.com", "Sam"), o => o.DeduplicationKey(finalKey));

// learn: end

Console.WriteLine($"first:  job {first.JobRef} ({first.Action})");
Console.WriteLine($"second: job {second.JobRef} ({second.Action})");
Console.WriteLine($"business key: {businessKey}");
Console.WriteLine($"final key:    {finalKey}");
Console.WriteLine(first.JobRef == second.JobRef ? "Same job - deduplicated." : "Different jobs.");
Console.WriteLine("Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.DeduplicationKey
{
    public sealed record SendWelcomeEmail(string Email, string Name);

    public static class WelcomeJob
    {
        [Job("send-welcome-email")]
        public static void Handle(SendWelcomeEmail input) => Console.WriteLine($"Sent welcome email to {input.Name} <{input.Email}>");
    }
}
```

