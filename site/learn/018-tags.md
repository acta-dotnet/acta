# How to label jobs so you can find them

Tags are name and value labels set on the enqueue, for filtering and grouping jobs in queries and in the dashboard.

- Section: Start jobs, step 16
- Program: `concepts/000-fundamentals/018-tags` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/018-tags
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Tag the job when you enqueue it

Tags are labels for filtering and grouping jobs in queries or a dashboard.

The lines that matter:

```csharp
var outcome = await jobs.EnqueueAsync(new SendWelcomeEmail("sam@example.com", "Sam"), o =>
    o.Tag("campaign", "spring").Tag("channel", "email")
);
```

In `tags.cs` (excerpt):

```csharp
var jobs = host.Services.GetRequiredService<IJobs>();

var outcome = await jobs.EnqueueAsync(new SendWelcomeEmail("sam@example.com", "Sam"), o =>
    o.Tag("campaign", "spring").Tag("channel", "email")
);

Console.WriteLine($"Enqueued job {outcome.JobRef} with tags campaign=spring, channel=email.");
Console.WriteLine("Worker is running - press Ctrl+C to stop.");
```

## 2. The job it acts on

`tags.cs` (excerpt):

```csharp
public sealed record SendWelcomeEmail(string Email, string Name);

public static class WelcomeJob
{
    [Job("send-welcome-email")]
    public static void Handle(SendWelcomeEmail input) => Console.WriteLine($"Sent welcome email to {input.Name} <{input.Email}>");
}
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/000-fundamentals/018-tags
```

Its output must contain:

```text
with tags campaign=spring, channel=email.
Sent welcome email to Sam <sam@example.com>
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to pass a large file by reference](/learn/025-large-payload-reference.md)

## More

- [The whole program](/learn/018-tags.program.md): every file as compiled, 43 lines.
