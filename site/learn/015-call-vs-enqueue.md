# How to choose between starting a job and waiting for it

`EnqueueAsync` returns as soon as the job is stored; `CallAsync` enqueues, waits, and returns the job's outcome.

- Section: Start jobs, step 13
- Program: `learn/000-fundamentals/015-call-vs-enqueue` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/015-call-vs-enqueue
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Start a job and carry on

EnqueueAsync returns immediately, before the job runs.

The lines that matter:

```csharp
var enqueued = await jobs.EnqueueAsync(new AddNumbers(1, 1));
```

In `call-vs-enqueue.cs` (excerpt):

```csharp
using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var enqueued = await jobs.EnqueueAsync(new AddNumbers(1, 1));
Console.WriteLine($"EnqueueAsync returned right away: job {enqueued.JobRef}, status {(await enqueued.GetStatusAsync())?.Status}");

var executed = await jobs.CallAsync(CallVsEnqueueJobs.AddNumbers(new AddNumbers(2, 2)));
Console.WriteLine($"CallAsync waited and returned: job {executed.JobId}, result {executed.ValueOrThrow().Sum}");

await host.StopAsync();
```

## 2. Start a job and wait for its result

CallAsync enqueues, then waits for the job and returns its outcome: `ValueOrThrow()` gives the result, or throws when the job failed or the wait timed out. It holds its caller that long, and a timeout stops only the wait; inside a handler, call `ctx.CallChildAsync` instead (213).

The lines that matter:

```csharp
var executed = await jobs.CallAsync(CallVsEnqueueJobs.AddNumbers(new AddNumbers(2, 2)));
```

In `call-vs-enqueue.cs` (excerpt):

```csharp
var jobs = host.Services.GetRequiredService<IJobs>();

var enqueued = await jobs.EnqueueAsync(new AddNumbers(1, 1));
Console.WriteLine($"EnqueueAsync returned right away: job {enqueued.JobRef}, status {(await enqueued.GetStatusAsync())?.Status}");

var executed = await jobs.CallAsync(CallVsEnqueueJobs.AddNumbers(new AddNumbers(2, 2)));
Console.WriteLine($"CallAsync waited and returned: job {executed.JobId}, result {executed.ValueOrThrow().Sum}");

await host.StopAsync();
```

## 3. The job it acts on

`call-vs-enqueue.cs` (excerpt):

```csharp
public static class AddNumbersJob
{
    [Job("add-numbers")]
    public static async Task<AddNumbersResult> Handle(AddNumbers input, CancellationToken ct)
    {
        await Task.Delay(500, ct);
        return new AddNumbersResult(input.Left + input.Right);
    }
}
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/000-fundamentals/015-call-vs-enqueue
```

Its output must contain:

```text
EnqueueAsync returned right away
CallAsync waited and returned
, result 4
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- The name in `[Job("add-numbers")]` and the namespace in `Run<CallVsEnqueueJobs>("call-vs-enqueue")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `AddNumbers` is stored with each job as its input, and `AddNumbersResult` as its result. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `add-numbers-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to enqueue many jobs in one round trip](/learn/019-batch-enqueue.md)

## More

- [The whole program](/learn/015-call-vs-enqueue.program.md): every file as compiled, 54 lines.
