# How to keep a repeated request from creating a second job

A deduplication key makes an enqueue idempotent: a second enqueue with the same key answers the job that is already there.

- Section: Start jobs, step 9
- Program: `learn/000-fundamentals/010-deduplication-key` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/010-deduplication-key
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Give the enqueue a key that names the request

`Deduplicate(businessKey)` on the raw builder stores the key with the job's name in front, so one business key used by two jobs never collides.

The lines that matter:

```csharp
var request = JobRequestBuilder
    .Create("deduplication-key", "send-welcome-email")
    .Json(new SendWelcomeEmail("sam@example.com", "Sam"))
    .Deduplicate(businessKey)
    .Build();

var first = await jobs.EnqueueAsync(request, CancellationToken.None);
```

In `deduplication-key.cs` (excerpt):

```csharp
var jobs = host.Services.GetRequiredService<IJobs>();

var businessKey = $"welcome:{Guid.NewGuid():N}";
var finalKey = DeduplicationKey.ForDefinition("send-welcome-email", businessKey);

var request = JobRequestBuilder
    .Create("deduplication-key", "send-welcome-email")
    .Json(new SendWelcomeEmail("sam@example.com", "Sam"))
    .Deduplicate(businessKey)
    .Build();

var first = await jobs.EnqueueAsync(request, CancellationToken.None);

var second = await jobs.EnqueueAsync(new SendWelcomeEmail("sam@example.com", "Sam"), o => o.DeduplicationKey(finalKey));
```

## 2. Repeat the enqueue and get the same job back

A typed enqueue stores its key exactly as given, so to meet the first job it passes the same composed key. Two typed enqueues need only pass the same string to `DeduplicationKey(...)`. This second enqueue answers the first job, deduplicated.

The lines that matter:

```csharp
var second = await jobs.EnqueueAsync(new SendWelcomeEmail("sam@example.com", "Sam"), o => o.DeduplicationKey(finalKey));
```

In `deduplication-key.cs` (excerpt):

```csharp
var first = await jobs.EnqueueAsync(request, CancellationToken.None);

var second = await jobs.EnqueueAsync(new SendWelcomeEmail("sam@example.com", "Sam"), o => o.DeduplicationKey(finalKey));
```

## 3. The job it acts on

`deduplication-key.cs` (excerpt):

```csharp
public static class WelcomeJob
{
    [Job("send-welcome-email")]
    public static void Handle(SendWelcomeEmail input) => Console.WriteLine($"Sent welcome email to {input.Name} <{input.Email}>");
}
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/000-fundamentals/010-deduplication-key
```

Its output must contain:

```text
Same job - deduplicated.
Sent welcome email to Sam <sam@example.com>
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- `handle.Action` says which happened: `EnqueueJobAction.Deduplicated` when the key matched a job already there, `Inserted` when this enqueue created it.
- The key prevents a second job row, not a second side effect. Execution stays at-least-once.
- Keys keep their case: `Order-1` and `order-1` are two keys, so lowercase an id yourself when its source ignores case.
- The name in `[Job("send-welcome-email")]` and the namespace in `Run<DeduplicationKeyJobs>("deduplication-key")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `SendWelcomeEmail` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `send-welcome-email-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to allow one job per hour or per day](/learn/028-deduplication-time-buckets.md)

## More

- [The whole program](/learn/010-deduplication-key.program.md): every file as compiled, 65 lines.
