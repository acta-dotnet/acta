# How to cut off an attempt that runs too long

`ExecutionTimeout` caps one attempt: when the time is up, the handler's `CancellationToken` trips and the attempt ends as a timeout.

- Section: Decide what happens when work fails, step 6
- Program: `concepts/300-failure-and-recovery/304-execution-timeout` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/304-execution-timeout
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Set the timeout on the job

ExecutionTimeout caps a single attempt: after 2s the framework trips the CancellationToken and the attempt ends as a timeout.

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
dotnet run --project concepts/300-failure-and-recovery/304-execution-timeout
```

Its output must contain:

```text
[q3] generating report (this runs too long)...
status=Failed
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to stop cleanly when a job is cancelled](/learn/008-cancellation-token.md)

## More

- [The whole program](/learn/304-execution-timeout.program.md): every file as compiled, 54 lines.
