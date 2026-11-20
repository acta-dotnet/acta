# How to enqueue many jobs in one round trip

`EnqueueJobsAsync` writes a whole list of jobs in one round trip to the database and answers their outcomes in request order.

- Section: Start jobs, step 14
- Program: `learn/000-fundamentals/019-batch-enqueue` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/019-batch-enqueue
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Build the requests and enqueue them together

EnqueueJobsAsync writes the whole batch in one round-trip; outcomes return in request order. Two requests with one deduplication key in the same batch throw before anything is written.

The lines that matter:

```csharp
var requests = Enumerable.Range(1, 500).Select(_ => new EnqueueJobRequest("batch-enqueue", "ping")).ToList();

var sw = Stopwatch.StartNew();
var outcomes = (await jobs.EnqueueJobsAsync(requests)).Jobs;
```

In `batch-enqueue.cs` (excerpt):

```csharp
using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var requests = Enumerable.Range(1, 500).Select(_ => new EnqueueJobRequest("batch-enqueue", "ping")).ToList();

var sw = Stopwatch.StartNew();
var outcomes = (await jobs.EnqueueJobsAsync(requests)).Jobs;
sw.Stop();
Console.WriteLine(
    $"Enqueued {outcomes.Count} jobs in one round-trip in {sw.ElapsedMilliseconds} ms (ids {outcomes[0].JobId}..{outcomes[^1].JobId})."
);
Console.WriteLine("Worker is running - press Ctrl+C to stop.");
```

## 2. The job it acts on

`batch-enqueue.cs` (excerpt):

```csharp
public static class PingJob
{
    [Job("ping")]
    public static void Handle() { }
}
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/000-fundamentals/019-batch-enqueue
```

Its output must contain:

```text
Enqueued 500 jobs in one round-trip
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- The name in `[Job("ping")]` and the namespace in `Run<BatchEnqueueJobs>("batch-enqueue")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to run urgent jobs first](/learn/017-priority.md)

## More

- [The whole program](/learn/019-batch-enqueue.program.md): every file as compiled, 48 lines.
