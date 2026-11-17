# How to decide what a failed firing does to its schedule

A firing that spends its retry budget returns the permanent job to `Idle`. `FiringFailurePolicy` says what happens to its schedules then: `Continue`, the default, fires the next occurrence as usual, and `Pause` pauses them, with the reason, until an operator resumes them.

- Section: Run work on a schedule, step 9
- Program: `learn/100-scheduling/111-firing-failure-policy` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/111-firing-failure-policy
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Pause the schedule when a firing fails for good

A firing retries under the job's budget like any attempt. The policy applies once that budget is spent; the second job below keeps the default, `Continue`. `FailAsync` in the bodies is the program's own helper that throws, so each attempt fails and retries; it is not `ctx.FailAsync`.

The lines that matter:

```csharp
[Job("sync-prices-pausing", MaxAttempts = 2, Backoff = "1s", FiringFailurePolicy = FiringFailurePolicyCode.Pause)]
```

In `firing-failure-policy.cs` (excerpt):

```csharp
private static readonly ConcurrentDictionary<string, int> FailedAttempts = new();

/// <summary>Firings of <paramref name="job"/> that spent their budget here: two failed attempts each.</summary>
public static int SpentFirings(string job) => FailedAttempts.GetValueOrDefault(job) / 2;

[Job("sync-prices-pausing", MaxAttempts = 2, Backoff = "1s", FiringFailurePolicy = FiringFailurePolicyCode.Pause)]
[Schedule("every-second", "PT1S")]
public Task Pausing(CancellationToken ct) => FailAsync("sync-prices-pausing");

[Job("sync-prices-continuing", MaxAttempts = 2, Backoff = "1s")]
[Schedule("every-second", "PT1S")]
public Task Continuing(CancellationToken ct) => FailAsync("sync-prices-continuing");
```

## 2. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/100-scheduling/111-firing-failure-policy
```

Its output must contain:

```text
then its schedule paused
and is still scheduled
sync-prices-continuing | active
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to start each firing of a recurring job clean](/learn/214-reset-state.md)

## More

- [The whole program](/learn/111-firing-failure-policy.program.md): every file as compiled, 112 lines.
