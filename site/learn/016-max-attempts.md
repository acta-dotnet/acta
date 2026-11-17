# How to limit how many times a job retries

`MaxAttempts` on the job is its retry budget. When the last attempt fails, the job ends as `Failed`.

- Section: Decide what happens when work fails, step 1
- Program: `concepts/000-fundamentals/016-max-attempts` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/016-max-attempts
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Set the retry budget on the job

MaxAttempts caps attempts, the first included; "0s" skips backoff. After the last attempt the job ends Failed.

The lines that matter:

```csharp
[Job("flaky-work", MaxAttempts = 3, Backoff = "0s")]
public static Task Handle(FlakyWork input) => throw new InvalidOperationException($"Could not process {input.Item}.");
```

In `max-attempts.cs` (excerpt):

```csharp
public sealed record FlakyWork(string Item);

public static class FlakyWorkJob
{
    [Job("flaky-work", MaxAttempts = 3, Backoff = "0s")]
    public static Task Handle(FlakyWork input) => throw new InvalidOperationException($"Could not process {input.Item}.");
}
```

## 2. Read how the job ended

The handler always throws; Acta makes up to MaxAttempts attempts, then transitions the job to Failed.

The lines that matter:

```csharp
var outcome = await jobs.EnqueueAsync(new FlakyWork("widget"));
await Task.Delay(1500);
var snapshot = await jobs.GetAsync(outcome);
Console.WriteLine($"status={snapshot!.Status} failureCount={snapshot.FailureCount}");
```

In `max-attempts.cs` (excerpt):

```csharp
var jobs = host.Services.GetRequiredService<IJobs>();

var outcome = await jobs.EnqueueAsync(new FlakyWork("widget"));
await Task.Delay(1500);
var snapshot = await jobs.GetAsync(outcome);
Console.WriteLine($"status={snapshot!.Status} failureCount={snapshot.FailureCount}");
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/000-fundamentals/016-max-attempts
```

Its output must contain:

```text
status=Failed failureCount=3
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- `FailureCount` counts the attempts that failed since the job last succeeded, a crash included. It reads 0 again when the job succeeds; the events keep every failure.
- A crash spends an attempt: the attempt a dead worker held is reclaimed as a failed one.
- With no `MaxAttempts`, a job gets 15 attempts, backing off from a minute to a day.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to set how long a job waits between retries](/learn/312-backoff-curve.md)

## More

- [The whole program](/learn/016-max-attempts.program.md): every file as compiled, 44 lines.
