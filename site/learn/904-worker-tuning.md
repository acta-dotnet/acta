# How to set how many jobs a worker runs at once

`MaxConcurrentExecutors` caps how many jobs one worker runs at once, and `SafetyPollInterval` bounds how long an idle worker sleeps between looks at the database.

- Section: Deploy and scale, step 9
- Program: `learn/900-runtime-and-tuning/904-worker-tuning` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/904-worker-tuning
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Cap how many jobs the worker runs at once

Cap simultaneous in-flight executions at Cap. Each claim takes only as many rows as there are free executors, so excess work stays Ready in the database until an executor frees. Fixed at startup; not autoscaled. The program sets `Cap` to 3.

The lines that matter:

```csharp
o.MaxConcurrentExecutors = Cap;
```

In `worker-tuning.cs` (excerpt):

```csharp
builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);

    j.ConfigureOptions(o =>
    {
        o.MaxConcurrentExecutors = Cap;

        o.SafetyPollInterval = TimeSpan.FromSeconds(1);
    });

    j.Run<WorkerTuningJobs>("worker-tuning");
});
```

## 2. Bound how long an idle worker sleeps

Upper bound on idle claim-loop sleep. 1s is the default and the minimum (the validator rejects lower values as a DB-traffic guard). An enqueue wakes the worker at once, so the interval only matters for work nothing announced.

The lines that matter:

```csharp
o.SafetyPollInterval = TimeSpan.FromSeconds(1);
```

In `worker-tuning.cs` (excerpt):

```csharp
builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);

    j.ConfigureOptions(o =>
    {
        o.MaxConcurrentExecutors = Cap;

        o.SafetyPollInterval = TimeSpan.FromSeconds(1);
    });

    j.Run<WorkerTuningJobs>("worker-tuning");
});
```

## 3. The job it acts on

`worker-tuning.cs` (excerpt):

```csharp
public static int Completed => Volatile.Read(ref _completed);
public static int PeakConcurrency => Volatile.Read(ref _peak);

// Each handler holds its executor for 50 ms - long enough that Cap parallel handlers
// overlap and the peak counter exceeds 1.
[Job("slow-task")]
public static async Task Handle(SlowTask input, CancellationToken ct)
{
    // Increment active count; update peak if this is a new high.
    var active = Interlocked.Increment(ref _active);
    int peak;
    do
    {
        peak = Volatile.Read(ref _peak);
        if (active <= peak)
            break;
    } while (Interlocked.CompareExchange(ref _peak, active, peak) != peak);

    Console.WriteLine($"[{input.Id}] started (active={active})");

    await Task.Delay(50, ct);

    Interlocked.Decrement(ref _active);
    Interlocked.Increment(ref _completed);
    Console.WriteLine($"[{input.Id}] done");
}
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/900-runtime-and-tuning/904-worker-tuning
```

Its output must contain:

```text
Peak observed concurrency: 3 (cap=3).
Concurrency cap respected: True
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- The name in `[Job("slow-task")]` and the namespace in `Run<WorkerTuningJobs>("worker-tuning")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `SlowTask` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `slow-task-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## More

- [The whole program](/learn/904-worker-tuning.program.md): every file as compiled, 104 lines.
