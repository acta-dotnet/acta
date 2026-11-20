# How to limit how many jobs run at once for one key

A `ConcurrencyKey` on the enqueue lets one job at a time run for that key. `ConcurrencyLimit` on the job gives a key several slots, and `RateLimit` meters how often attempts are admitted.

- Section: Control order and load, step 2
- Program: `learn/200-durable-execution/209-concurrency-key` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/209-concurrency-key
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Give jobs that must not overlap the same key

Shared concurrency key serializes whole jobs: the key is taken after claim, before the handler; a worker claiming the second job while it's held releases back to Ready (no executor tied up). Contrast 207-run-with-lock, which serializes only a section while the waiter holds its executor.

The lines that matter:

```csharp
var first = await jobs.EnqueueAsync(new RebuildIndex("acme"), o => o.ConcurrencyKey("rebuild:acme"));
var second = await jobs.EnqueueAsync(new RebuildIndex("acme"), o => o.ConcurrencyKey("rebuild:acme"));
```

In `concurrency-key.cs` (excerpt):

```csharp
using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var first = await jobs.EnqueueAsync(new RebuildIndex("acme"), o => o.ConcurrencyKey("rebuild:acme"));
var second = await jobs.EnqueueAsync(new RebuildIndex("acme"), o => o.ConcurrencyKey("rebuild:acme"));
Console.WriteLine("Enqueued two jobs with the same concurrency key.");
```

## 2. Let several run at once, up to a limit

ConcurrencyLimit is a per-definition policy value, so an operator can raise or lower it on the definitions row without a deploy. Two at a time, whatever the fleet size.

The lines that matter:

```csharp
[Job("reindex-shard", ConcurrencyLimit = 2)]
```

In `concurrency-key.cs` (excerpt):

```csharp
public sealed class ReindexShardJob
{
    [Job("reindex-shard", ConcurrencyLimit = 2)]
    public async Task Handle(ReindexShard input, CancellationToken ct)
    {
        Console.WriteLine($"shard {input.Shard} started");
        await Task.Delay(1000, ct);
        Console.WriteLine($"shard {input.Shard} finished");
    }
}
```

## 3. Limit how often jobs start

RateLimit is the other admission gate: five admissions a second across the namespace's workers, every execution taking a turn, a retry included. It is metered on the definition name because no RateName was given. Like ConcurrencyLimit it is an operator-overridable policy value on the definitions row.

The lines that matter:

```csharp
[Job("ping-endpoint", RateLimit = "5/s")]
```

In `concurrency-key.cs` (excerpt):

```csharp
public sealed class PingEndpointJob
{
    [Job("ping-endpoint", RateLimit = "5/s")]
    public Task Handle(PingEndpoint input, CancellationToken ct)
    {
        Console.WriteLine($"ping {input.Sequence} at {DateTime.UtcNow:HH:mm:ss.fff}");
        return Task.CompletedTask;
    }
}
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/200-durable-execution/209-concurrency-key
```

Its output must contain:

```text
[acme] rebuild #2 finished
shard 2 finished
shard 3 finished
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- A key is mutual exclusion, never ordering: an older job can be overtaken while the key stays busy. For one at a time in order, use a lane.
- A job that finds its key full is handed back and re-armed a couple of seconds out. Each such bounce is an execution of its own and adds rows to the timeline.
- A slot is held while an attempt runs, not while the job sleeps or waits. A slot held by a worker that died frees when that worker's lease lapses; recovery reclaims the job after that.
- The names in `[Job("ping-endpoint")]`, `[Job("reindex-shard")]` and `[Job("rebuild-index")]`, and the namespace in `Run<ConcurrencyKeyJobs>("concurrency-key")`, are stored with every job. Treat them as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `PingEndpoint` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `ping-endpoint-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to apply each customer's commands in order while other customers keep moving](/learn/223-lane-per-customer.md)

## More

- [The whole program and its notes](/learn/209-concurrency-key.program.md): every file as compiled, 256 lines, and the notes on the design, the rows to inspect, and how to break it.
