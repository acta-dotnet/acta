# The whole program: keep a repeated request from creating a second job

The how-to is [/learn/010-idempotency-key.md](/learn/010-idempotency-key.md). This file holds every source file of `learn/000-fundamentals/010-idempotency-key` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### idempotency-key.cs

```csharp
// How to keep a repeated request from creating a second job
// With an idempotency key, a second enqueue with the same key answers the job that is already there.
using Acta;
using Acta.Learn.IdempotencyKey;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<IdempotencyKeyJobs>("idempotency-key");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var businessKey = $"welcome:{Guid.NewGuid():N}";
var finalKey = IdempotencyKey.ForDefinition("send-welcome-email", businessKey);

// learn: 1. Give the enqueue a key that names the request
// `Deduplicate(businessKey)` on the raw builder stores the key with the job's name in front, so one business key
// used by two jobs never collides.
var request = JobRequestBuilder
    .Create("idempotency-key", "send-welcome-email")
    .Json(new SendWelcomeEmail("sam@example.com", "Sam"))
    .Deduplicate(businessKey)
    .Build();

var first = await jobs.EnqueueAsync(request, CancellationToken.None);

// learn: end

// learn: 2. Repeat the enqueue and get the same job back
// A typed enqueue stores its key exactly as given, so to meet the first job it passes the same composed key. Two
// typed enqueues need only pass the same string to `IdempotencyKey(...)`. This second enqueue answers the first
// job, deduplicated.
var second = await jobs.EnqueueAsync(new SendWelcomeEmail("sam@example.com", "Sam"), o => o.IdempotencyKey(finalKey));

// learn: end

Console.WriteLine($"first:  job {first.JobRef} ({first.Action})");
Console.WriteLine($"second: job {second.JobRef} ({second.Action})");
Console.WriteLine($"business key: {businessKey}");
Console.WriteLine($"final key:    {finalKey}");
Console.WriteLine(first.JobRef == second.JobRef ? "Same job - deduplicated." : "Different jobs.");
Console.WriteLine("Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.IdempotencyKey
{
    public sealed record SendWelcomeEmail(string Email, string Name);

    public static class WelcomeJob
    {
        [Job("send-welcome-email")]
        public static void Handle(SendWelcomeEmail input) => Console.WriteLine($"Sent welcome email to {input.Name} <{input.Email}>");
    }
}
```

