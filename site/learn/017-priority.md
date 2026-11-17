# How to run urgent jobs first

A priority on the enqueue decides which of the jobs that are due at once a worker claims first.

- Section: Start jobs, step 15
- Program: `learn/000-fundamentals/017-priority` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/017-priority
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Set the priority when you enqueue

`Priority` is an option of the enqueue. When several jobs are due at once, the worker claims the one with the higher priority first, whichever arrived first.

The lines that matter:

```csharp
await jobs.EnqueueAsync(new GenerateReport("URGENT"), o => o.Priority(JobPriorityCode.High).NextRunAt(together));
```

In `priority.cs` (excerpt):

```csharp
foreach (var n in new[] { 2, 3, 4, 5 })
{
    await jobs.EnqueueAsync(new GenerateReport($"bulk-{n}"), o => o.Priority(JobPriorityCode.Bulk).NextRunAt(together));
}

await jobs.EnqueueAsync(new GenerateReport("URGENT"), o => o.Priority(JobPriorityCode.High).NextRunAt(together));
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();
```

## 2. The job it acts on

`priority.cs` (excerpt):

```csharp
public static class ReportJob
{
    [Job("generate-report")]
    public static async Task Handle(GenerateReport input, CancellationToken ct)
    {
        Console.WriteLine($"running {input.Name}");
        await Task.Delay(400, ct);
    }
}
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/000-fundamentals/017-priority
```

Its output must contain:

```text
running URGENT
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to label jobs so you can find them](/learn/018-tags.md)

## More

- [The whole program](/learn/017-priority.program.md): every file as compiled, 57 lines.
