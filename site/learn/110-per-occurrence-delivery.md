# How to make every occurrence its own job

`ScheduledDelivery = PerOccurrence` makes each occurrence a root job of its own, with its own ref, retries, result, and retention. `MaxOutstanding` bounds how many may be unfinished at once.

- Section: Run work on a schedule, step 8
- Program: `learn/100-scheduling/110-per-occurrence-delivery` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/110-per-occurrence-delivery
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Deliver each occurrence as a job of its own

Each occurrence is an ordinary root job with a ref, retries, and result of its own. `MaxOutstanding` bounds how many may be unfinished at once, and a full schedule set to `Drop` skips an occurrence, audited as `schedule.skipped` with the reason `capacity`.

The lines that matter:

```csharp
[Job("import-files", ScheduledDelivery = ScheduledDeliveryCode.PerOccurrence)]
[Schedule("every-second", "PT1S", MaxOutstanding = 2, FullBehavior = ScheduleFullBehaviorCode.Drop)]
```

In `per-occurrence-delivery.cs` (excerpt):

```csharp
public static int AtOnce => Volatile.Read(ref _atOnce);

public static int MostAtOnce => Volatile.Read(ref _mostAtOnce);

public static void Release() => Gate.TrySetResult();

[Job("import-files", ScheduledDelivery = ScheduledDeliveryCode.PerOccurrence)]
[Schedule("every-second", "PT1S", MaxOutstanding = 2, FullBehavior = ScheduleFullBehaviorCode.Drop)]
public async Task Import(JobContext ctx, CancellationToken ct)
{
    lock (Counting)
    {
        _mostAtOnce = Math.Max(_mostAtOnce, ++_atOnce);
    }
    try
    {
        await Gate.Task.WaitAsync(ct);
        Finished.Enqueue(ctx.JobRef);
    }
    finally
    {
        lock (Counting)
        {
            _atOnce--;
        }
    }
}
```

## 2. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/100-scheduling/110-per-occurrence-delivery
```

Its output must contain:

```text
different jobs, one per occurrence
at most 2 were unfinished at once
were dropped while full, each audited as schedule.skipped with the reason capacity
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on any of the three databases and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to decide what a failed firing does to its schedule](/learn/111-firing-failure-policy.md)

## More

- [The whole program](/learn/110-per-occurrence-delivery.program.md): every file as compiled, 142 lines.
