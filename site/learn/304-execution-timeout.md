# How to cut off an attempt that runs too long

`ExecutionTimeout` caps one attempt: when the time is up, the handler's `CancellationToken` trips and the cut spends an attempt.

- Section: Decide what happens when work fails, step 6
- Program: `learn/300-failure-and-recovery/304-execution-timeout` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/304-execution-timeout
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Set the timeout on the job

ExecutionTimeout caps a single attempt: after 2s the framework trips the CancellationToken. The cut spends an attempt, and with MaxAttempts = 1 the job ends `Failed`.

The lines that matter:

```csharp
[Job("slow-report", ExecutionTimeout = "2s", MaxAttempts = 1)]
```

In `execution-timeout.cs` (excerpt):

```csharp
public sealed class SlowReportJob
{
    [Job("slow-report", ExecutionTimeout = "2s", MaxAttempts = 1)]
    public async Task Handle(SlowReport input, JobContext context, CancellationToken ct)
    {
        Console.WriteLine($"[{input.Name}] generating report (this runs too long)...");
        await Task.Delay(TimeSpan.FromSeconds(10), ct); // exceeds the 2s timeout, so ct trips here
        Console.WriteLine($"[{input.Name}] report done"); // never reached
    }
}
```

## 2. Pass the token to the work, so the timeout can stop it

The lines that matter:

```csharp
await Task.Delay(TimeSpan.FromSeconds(10), ct); // exceeds the 2s timeout, so ct trips here
```

In `execution-timeout.cs` (excerpt):

```csharp
public sealed class SlowReportJob
{
    [Job("slow-report", ExecutionTimeout = "2s", MaxAttempts = 1)]
    public async Task Handle(SlowReport input, JobContext context, CancellationToken ct)
    {
        Console.WriteLine($"[{input.Name}] generating report (this runs too long)...");
        await Task.Delay(TimeSpan.FromSeconds(10), ct); // exceeds the 2s timeout, so ct trips here
        Console.WriteLine($"[{input.Name}] report done"); // never reached
    }
}
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/300-failure-and-recovery/304-execution-timeout
```

Its output must contain:

```text
[q3] generating report (this runs too long)...
status=Failed
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- The name in `[Job("slow-report")]` and the namespace in `Run<ExecutionTimeoutJobs>("execution-timeout")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `SlowReport` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `slow-report-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to stop cleanly when a job is cancelled](/learn/008-cancellation-token.md)

## More

- [The whole program](/learn/304-execution-timeout.program.md): every file as compiled, 54 lines.
