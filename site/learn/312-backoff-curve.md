# How to set how long a job waits between retries

`Backoff` on the job sets how the wait between retries grows: `1s..8s x2 exact` starts at one second, doubles with each failure, and stops growing at eight.

- Section: Decide what happens when work fails, step 2
- Program: `concepts/300-failure-and-recovery/312-backoff-curve` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/312-backoff-curve
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Write the backoff on the job

Exponential backoff: delays double per failure (1s, 2s, 4s) up to MaxAttempts=4. exact makes the curve deterministic so NextRunAtUtc gaps are observable.

The lines that matter:

```csharp
[Job("flaky-transfer", MaxAttempts = 4, Backoff = "1s..8s x2 exact")]
```

In `backoff-curve.cs` (excerpt):

```csharp
public sealed record FlakyTransfer(string Id);

public static class FlakyTransferJob
{
    [Job("flaky-transfer", MaxAttempts = 4, Backoff = "1s..8s x2 exact")]
    public static Task Handle(FlakyTransfer input) =>
        throw new InvalidOperationException($"Transfer {input.Id} failed: upstream unavailable.");
}
```

## 2. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/300-failure-and-recovery/312-backoff-curve
```

Its output must contain:

```text
failure #3: next retry at
job ended: status=Failed failureCount=4
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- With no `Backoff`, retries back off from a minute to a day over 15 attempts, about 4.4 days in all.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to fail a job for good, with no retry](/learn/301-fail-job.md)

## More

- [The whole program](/learn/312-backoff-curve.program.md): every file as compiled, 74 lines.
