# The whole program: start child jobs and wait for them

The how-to is [/learn/211-child-jobs.md](/learn/211-child-jobs.md). This file holds every source file of `learn/200-durable-execution/211-child-jobs` as it is compiled, and the notes kept beside the code.

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### child-jobs.cs

```csharp
// How to start child jobs and wait for them
// A parent starts child jobs by name, suspends without holding a worker until every child has finished, and reads
// their results.
using Acta;
using Acta.Labs;
using Acta.Learn.ChildJobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
var lab = new LearnLab(builder.Configuration, args);
var failChild = args.Contains("--fail-child", StringComparer.OrdinalIgnoreCase);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ChildJobsManifest>("child-jobs");
});
if (failChild)
{
    // The two child failures are the experiment; their durable rows/events are clearer than startup stack traces.
    builder.Logging.AddFilter("Acta", LogLevel.Error);
}

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// Children dedupe by name; WaitChildrenAsync resumes from durable latches on replay.
var outcome = await jobs.EnqueueAsync(new BuildSnowman("Frosty", failChild ? "head" : null));
await WaitForAsync(
    jobs,
    outcome.JobId,
    static snapshot => snapshot.Status == JobStatusCode.Suspended,
    "parent to suspend on child latches"
);
await lab.ShowAllAsync(
    "Explore every complete job record in the lineage",
    """
    SELECT *
    FROM jobs_view
    WHERE lineage_root_job_id = @rootJobId
    ORDER BY job_id
    """,
    new { rootJobId = outcome.JobId }
);
await lab.ShowAsync(
    "Parent and children are independent jobs in one lineage",
    """
    SELECT job_id, definition_name, job_status, parent_job_id, lineage_root_job_id, leased_by_worker_id
    FROM jobs_view
    WHERE lineage_root_job_id = @rootJobId
    ORDER BY job_id
    """,
    new { rootJobId = outcome.JobId }
);
await lab.ShowAsync(
    "The waiting parent owns child checkpoints and no worker",
    """
    SELECT checkpoint_name, checkpoint_kind, checkpoint_status
    FROM checkpoints_view
    WHERE job_id = @parentJobId
    ORDER BY checkpoint_name
    """,
    new { parentJobId = outcome.JobId }
);

var terminal = await WaitForAsync(jobs, outcome.JobId, static snapshot => snapshot.Status.IsTerminal, "parent to reach a terminal state");
if (terminal.Status == JobStatusCode.Succeeded)
{
    var result = await jobs.GetResultAsync<FinishedSnowman>(outcome);
    Console.WriteLine($"snowman finished: {result}");
}
else if (failChild && terminal.Status == JobStatusCode.Failed)
{
    Console.WriteLine("Expected failure mode: the child failed independently and the parent recorded the failed latch outcome.");
}
else
{
    throw new InvalidOperationException($"Parent {terminal.JobRef} ended {terminal.Status}; inspect its events for the reason.");
}
await lab.ShowAsync(
    "Closed latches release the parent for a later execution",
    """
    SELECT job_id, definition_name, job_status, execution_number, parent_job_id
    FROM jobs_view
    WHERE lineage_root_job_id = @rootJobId
    ORDER BY job_id
    """,
    new { rootJobId = outcome.JobId }
);
await lab.ShowAsync(
    "Every child outcome is now latched on the parent",
    """
    SELECT checkpoint_name, checkpoint_kind, checkpoint_status
    FROM checkpoints_view
    WHERE job_id = @parentJobId
    ORDER BY checkpoint_name
    """,
    new { parentJobId = outcome.JobId }
);
await lab.ShowAsync(
    "Parent and child events retain the independent failure history",
    """
    SELECT entity_id AS job_id, definition_name, event, execution_number, details_text
    FROM events_view
    WHERE lineage_root_job_id = @rootJobId
    ORDER BY event_id
    """,
    new { rootJobId = outcome.JobId }
);

await host.StopAsync();

static async Task<GetJobResponse> WaitForAsync(IJobs jobs, long jobId, Func<GetJobResponse, bool> predicate, string description)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try
    {
        while (true)
        {
            var snapshot = await jobs.GetAsync(JobLookup.ById(jobId), timeout.Token);
            if (snapshot is not null && predicate(snapshot))
            {
                return snapshot;
            }
            if (snapshot?.Status.IsTerminal == true)
            {
                throw new InvalidOperationException($"Job {snapshot.JobRef} became {snapshot.Status} while waiting for {description}.");
            }
            await Task.Delay(50, timeout.Token);
        }
    }
    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
    {
        throw new TimeoutException($"Timed out waiting for {description} on job {jobId}.");
    }
}

namespace Acta.Learn.ChildJobs
{
    public sealed record BuildSnowman(string Name, string? FailPart);

    public sealed record RollSnowball(string Part, bool ShouldFail);

    public sealed record Snowball(string Part, int Diameter);

    public sealed record FinishedSnowman(string Name, int HeightCm);

    public sealed class SnowmanBuilder
    {
        // EnqueueChildAsync enqueues name-deduped child jobs; WaitChildrenAsync suspends the parent
        // (holding no executor) until every child is terminal; cancelling the parent cascades to children.
        // MapAsync does the same for a list of children in one call (lab 215).
        [Job("build-snowman")]
        public async Task<FinishedSnowman> Handle(BuildSnowman input, JobContext context, CancellationToken ct)
        {
            // learn: 1. Start each child under a name
            // On a replay of the parent, the same name answers the child that is already there, so no child starts
            // twice.
            var bottom = await context.EnqueueChildAsync("bottom", new RollSnowball("bottom", input.FailPart == "bottom"), ct);
            var middle = await context.EnqueueChildAsync("middle", new RollSnowball("middle", input.FailPart == "middle"), ct);
            var head = await context.EnqueueChildAsync("head", new RollSnowball("head", input.FailPart == "head"), ct);
            // learn: end

            // learn: 2. Wait for all of them, and decide what a failed child means
            // The parent parks, holding no executor, and wakes as the children it waits on land, so it can run again
            // more than once before every child is terminal. A failed child does not throw here: the wait lists it,
            // and the parent decides. `FailAsync` ends the handler there, so the results are read only when all
            // three succeeded.
            var joined = await context.WaitChildrenAsync([bottom, middle, head], ct);
            foreach (var outcome in joined.Failed)
            {
                await context.FailAsync($"child {outcome.JobRef} ended {outcome.Status}; see its job events for the reason", ct);
            }
            // learn: end

            // learn: 3. Read each child's result
            var b = await context.GetChildResultAsync<Snowball>(bottom.JobId, ct);
            var m = await context.GetChildResultAsync<Snowball>(middle.JobId, ct);
            var h = await context.GetChildResultAsync<Snowball>(head.JobId, ct);
            // learn: end

            var height = b.Diameter + m.Diameter + h.Diameter;
            Console.WriteLine($"[{input.Name}] stacked {b.Diameter} + {m.Diameter} + {h.Diameter} = {height}cm tall");
            return new FinishedSnowman(input.Name, height);
        }

        [Job("roll-snowball", MaxAttempts = 2, Backoff = "0s")]
        public async Task<Snowball> Roll(RollSnowball input, CancellationToken ct)
        {
            await Task.Delay(input.Part == "bottom" ? 2000 : 1500, ct);
            if (input.ShouldFail)
            {
                throw new InvalidOperationException($"simulated failure while rolling the {input.Part} snowball");
            }
            var diameter = input.Part switch
            {
                "bottom" => 60,
                "middle" => 40,
                _ => 20,
            };
            Console.WriteLine($"  rolled {input.Part} snowball ({diameter}cm)");
            return new Snowball(input.Part, diameter);
        }
    }
}
```

## Notes

### The problem

Parallel sub-work needs independent execution and failure evidence, while the parent must wait without
holding a process or worker.

### Common approaches

- Run tasks in memory and join before the handler returns.
- Use durable steps inside one job identity.
- Create independently claimable child jobs and durable parent-owned latches.
- Move the graph to an external workflow engine.

### Why this design

Acta children are normal jobs with their own retries, workers, results, and events. The parent suspends
without a lease; child terminal outcomes set checkpoints owned by the parent, which later re-enters and
joins the results.

This lab enqueues each child with `EnqueueChildAsync` and waits for all of them with one `WaitChildrenAsync`, so
every latch is visible. When the children come from a list, `MapAsync` does the same over the same latches
in one call; lab 215 shows both, and lab 212 uses `MapAsync` for a map-reduce.

### Trade-offs

Independent jobs create more rows, claims, serialization, and failure policy than steps. The parent
handler still replays, and child names/latches are durable contract keys.

### Run the experiment

```bash
dotnet run --project learn/200-durable-execution/211-child-jobs
```

### Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

While children run, `jobs_view` shows a suspended, unleased parent plus independently executing children
linked by `parent_job_id`/`lineage_root_job_id`. `checkpoints_view` shows pending child latches. The final
snapshot proves the parent resumed on a later execution.

### Break it

Run the built-in child failure:

```bash
dotnet run --project learn/200-durable-execution/211-child-jobs -- --fail-child
```

The head child exhausts two attempts, the parent resumes from its latch and ends failed, and the program
prints both terminal rows instead of waiting only for success. Expected framework stack traces are
suppressed; `events_view` retains the complete failure evidence. Continue with lab 219 for explicit
child-failure outcome policy.

### When not to use

Prefer steps when work belongs to one identity, needs no independent ownership, and row/claim overhead
matters. Prefer child jobs for separate retries, parallelism, observability, or worker namespaces. Use a
workflow engine for very large or externally authored graphs.

### Source trail

- [The related Engineering Lab](https://github.com/acta-dotnet/acta/blob/main/docs/engineering-labs.md)
- [`child-jobs.cs`](https://github.com/acta-dotnet/acta/blob/main/learn/200-durable-execution/211-child-jobs/child-jobs.cs)
- [`JobContext.cs`](https://github.com/acta-dotnet/acta/blob/main/src/Acta/Handlers/JobContext.cs)
- [`ListStaleJobChildLatches.sqlite.sql`](https://github.com/acta-dotnet/acta/blob/main/src/Acta/Operations/Execution/ListStaleJobChildLatches/ListStaleJobChildLatches.sqlite.sql)
