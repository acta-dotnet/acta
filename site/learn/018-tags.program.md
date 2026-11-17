# The whole program: label jobs so you can find them

The how-to is [/learn/018-tags.md](/learn/018-tags.md). This file holds every source file of `learn/000-fundamentals/018-tags` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### tags.cs

```csharp
// How to label jobs so you can find them
// Tags are name and value labels set on the enqueue, for filtering and grouping jobs in queries and in the dashboard.
using Acta;
using Acta.Learn.Tags;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<TagsJobs>("tags");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// learn: Tag the job when you enqueue it
// Tags are labels for filtering and grouping jobs in queries or a dashboard. Find them with
// `operations.Ledger.ListJobsAsync(new ListJobsQuery(Tags: [new TagFilter("campaign", "spring")]))`.
var outcome = await jobs.EnqueueAsync(new SendWelcomeEmail("sam@example.com", "Sam"), o =>
    o.Tag("campaign", "spring").Tag("channel", "email")
);

// learn: end
Console.WriteLine($"Enqueued job {outcome.JobRef} with tags campaign=spring, channel=email.");
Console.WriteLine("Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.Tags
{
    public sealed record SendWelcomeEmail(string Email, string Name);

    public static class WelcomeJob
    {
        [Job("send-welcome-email")]
        public static void Handle(SendWelcomeEmail input) => Console.WriteLine($"Sent welcome email to {input.Name} <{input.Email}>");
    }
}
```

