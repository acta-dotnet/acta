# How to show where each job is on your own page

`IJobs` reads one job for a page of your own: its status, a value the handler keeps for the screen, and a sentence that says why the job is where it is.

- Section: Watch and control running work, step 3
- Program: `learn/400-observability-and-alerts/415-own-status-page` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/415-own-status-page
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Keep what the screen shows in a variable

The status says where the job is in Acta's terms. What it is doing in your terms is a variable the handler writes as the work advances, inside the step, so a replay does not write it again.

The lines that matter:

```csharp
await ctx.SetVariableAsync("stage", "charging", inner);
```

In `own-status-page.cs` (excerpt):

```csharp
[Job("prepare-order", MaxAttempts = 3, Backoff = "2s")]
public async Task Handle(PrepareOrder order, JobContext ctx, CancellationToken ct)
{
    await ctx.RunStepAsync(
        "charge",
        async inner =>
        {
            await ctx.SetVariableAsync("stage", "charging", inner);
            if (ctx.ExecutionNumber == 1)
            {
                throw new InvalidOperationException("the payment provider timed out");
            }
        },
        ct
    );

    await ctx.RunStepAsync("hand-to-warehouse", async inner => await ctx.SetVariableAsync("stage", "packing", inner), ct);
    await ctx.WaitSignalAsync("packed", ct);
    await ctx.SetVariableAsync("stage", "shipped", ct);
}
```

## 2. Read the job, its stage, and the sentence that says why

`GetAsync` answers the job's row, `GetVariableOrDefaultAsync` the value its handler keeps for the screen, and `ExplainAsync` a sentence a person can read. Each is a read of what the database holds now; none waits.

The lines that matter:

```csharp
var job = (await jobs.GetAsync(order))!;
var stage = await jobs.GetVariableOrDefaultAsync<string>(order, "stage");
var why = (await jobs.ExplainAsync(order))!;
```

In `own-status-page.cs` (excerpt):

```csharp
await host.StopAsync();

async Task ShowAsync()
{
    var job = (await jobs.GetAsync(order))!;
    var stage = await jobs.GetVariableOrDefaultAsync<string>(order, "stage");
    var why = (await jobs.ExplainAsync(order))!;

    var shown = job.Status switch
    {
        JobStatusCode.Suspended => "waiting",
        JobStatusCode.Ready when job.FailureCount > 0 => "retrying",
        JobStatusCode.Executing when job.LeaseLapsed => "interrupted",
        JobStatusCode.Executing => "running",
        _ => job.Status.Code,
    };
    Console.WriteLine($"{shown, -9} | stage: {stage, -8} | {why.Headline}");
}
```

## 3. Say the status in the page's own words

A waiting job is healthy, a ready job that has failed before is between retries, and `Executing` means claimed, not that a process is alive: `LeaseLapsed` is true for a job whose worker stopped heartbeating.

The lines that matter:

```csharp
var shown = job.Status switch
{
    JobStatusCode.Suspended => "waiting",
    JobStatusCode.Ready when job.FailureCount > 0 => "retrying",
    JobStatusCode.Executing when job.LeaseLapsed => "interrupted",
    JobStatusCode.Executing => "running",
    _ => job.Status.Code,
};
```

In `own-status-page.cs` (excerpt):

```csharp
async Task ShowAsync()
{
    var job = (await jobs.GetAsync(order))!;
    var stage = await jobs.GetVariableOrDefaultAsync<string>(order, "stage");
    var why = (await jobs.ExplainAsync(order))!;

    var shown = job.Status switch
    {
        JobStatusCode.Suspended => "waiting",
        JobStatusCode.Ready when job.FailureCount > 0 => "retrying",
        JobStatusCode.Executing when job.LeaseLapsed => "interrupted",
        JobStatusCode.Executing => "running",
        _ => job.Status.Code,
    };
    Console.WriteLine($"{shown, -9} | stage: {stage, -8} | {why.Headline}");
}
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/400-observability-and-alerts/415-own-status-page
```

Its output must contain:

```text
retrying  | stage: charging
waiting   | stage: packing
succeeded | stage: shipped
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- `executing` means claimed, not that a process is alive: `LeaseLapsed` is true for an executing job whose worker stopped heartbeating.
- A sleeping job reads `ready` with a future `NextRunAtUtc`; a job waiting for a signal or a child reads `suspended`. Both are healthy.
- `ExecutionNumber` counts every claim of the job, each resume after a wait and each bounce off a full concurrency key included. Show `FailureCount` as the tries.
- The text of a status is `job.Status.Code`, such as `suspended`. `ToString()` gives the C# name, `Suspended`.
- Write the screen's value inside a step. A bare write before a wait runs again when the handler is entered again, and the page would step back for a moment.
- These calls read one job. To find many, [list and page through jobs](/learn/409-operator-queries.md).
- `"charge"` and `"hand-to-warehouse"` are the keys to each step's stored result. Rename `"charge"` and an order that was already charged is charged again.
- `"packed"` names the signal an order waits on, and `"stage"` is the key the status page reads. A renamed signal is a different signal, and a renamed variable reads as absent.
- A job that waits for a signal or a timer can stay stored for days. The name in `[Job("prepare-order")]` and the namespace in `Run<OwnStatusPageJobs>("own-status-page")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `PrepareOrder` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `prepare-order-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Check yourself

1. A job reads `executing`, its worker was killed, and its lease has run out. What should the page show?
   - a) Running
   - b) Interrupted
   - c) Failed
   - Answer: b. `executing` says a worker claimed the job, not that the worker is alive. `LeaseLapsed` is true once its lease has run out, and recovery brings the job back by itself; nothing has failed.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to read why a job did what it did](/learn/404-read-event-timeline.md)

## More

- [The whole program](/learn/415-own-status-page.program.md): every file as compiled, 105 lines.
