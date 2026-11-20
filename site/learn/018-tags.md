# How to label jobs so you can find them

Tags are name and value labels set on the enqueue, for filtering and grouping jobs in queries and in the dashboard.

- Section: Start jobs, step 16
- Program: `learn/000-fundamentals/018-tags` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/018-tags
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Tag the job when you enqueue it

Tags are labels for filtering and grouping jobs in queries or a dashboard. Find them with `operations.Ledger.ListJobsAsync(new ListJobsQuery(Tags: [new TagFilter("campaign", "spring")]))`.

The lines that matter:

```csharp
var outcome = await jobs.EnqueueAsync(new SendWelcomeEmail("sam@example.com", "Sam"), o =>
    o.Tag("campaign", "spring").Tag("channel", "email")
);
```

In `tags.cs` (excerpt):

```csharp
using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var outcome = await jobs.EnqueueAsync(new SendWelcomeEmail("sam@example.com", "Sam"), o =>
    o.Tag("campaign", "spring").Tag("channel", "email")
);
Console.WriteLine($"Enqueued job {outcome.JobRef} with tags campaign=spring, channel=email.");
Console.WriteLine("Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();
```

## 2. The job it acts on

`tags.cs` (excerpt):

```csharp
public static class WelcomeJob
{
    [Job("send-welcome-email")]
    public static void Handle(SendWelcomeEmail input) => Console.WriteLine($"Sent welcome email to {input.Name} <{input.Email}>");
}
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/000-fundamentals/018-tags
```

Its output must contain:

```text
with tags campaign=spring, channel=email.
Sent welcome email to Sam <sam@example.com>
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- The name in `[Job("send-welcome-email")]` and the namespace in `Run<TagsJobs>("tags")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `SendWelcomeEmail` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `send-welcome-email-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to pass a large file by reference](/learn/025-large-payload-reference.md)

## More

- [The whole program](/learn/018-tags.program.md): every file as compiled, 44 lines.
