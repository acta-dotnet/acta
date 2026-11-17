# The whole program: wait for time to pass without a running process

The how-to is [/learn/205-durable-sleep.md](/learn/205-durable-sleep.md). This file holds every source file of `learn/200-durable-execution/205-durable-sleep` as it is compiled, and the notes kept beside the code.

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on any of the three databases and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### durable-sleep.cs

```csharp
// How to wait for time to pass without a running process
// A named sleep stores the due time, frees the worker, and wakes the job when the time comes.
using Acta;
using Acta.Labs;
using Acta.Learn.DurableSleep;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

const string jobNamespace = "durable-sleep";
var commandIndex = Array.FindIndex(args, static arg =>
    arg.Equals("start", StringComparison.OrdinalIgnoreCase)
    || arg.Equals("inspect", StringComparison.OrdinalIgnoreCase)
    || arg.Equals("recover", StringComparison.OrdinalIgnoreCase)
);
var command = commandIndex < 0 ? "run" : args[commandIndex].ToLowerInvariant();
var jobRefIndex = command is "inspect" or "recover" ? commandIndex + 1 : -1;
if (jobRefIndex >= args.Length || (jobRefIndex >= 0 && !JobRef.TryParse(args[jobRefIndex], out _)))
{
    Console.WriteLine($"Usage: dotnet run --project learn/200-durable-execution/205-durable-sleep -- {command} <job-ref>");
    return;
}

var hostArgs = args.Where((_, index) => index != commandIndex && index != jobRefIndex).ToArray();
var builder = Host.CreateApplicationBuilder(hostArgs);
var lab = new LearnLab(builder.Configuration, args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    if (command == "inspect")
    {
        j.Reference<DurableSleepJobs>(jobNamespace);
    }
    else
    {
        j.Run<DurableSleepJobs>(jobNamespace);
    }
});

using var host = builder.Build();
await host.StartAsync();
var jobs = host.Services.GetRequiredService<IJobs>();

if (command == "inspect")
{
    var inspected = await RequireJobAsync(jobs, args[jobRefIndex]);
    await ShowTimerStateAsync(lab, inspected.JobId);
    await host.StopAsync();
    return;
}

if (command == "recover")
{
    var existing = await RequireJobAsync(jobs, args[jobRefIndex]);
    Console.WriteLine($"Recovering existing durable identity {existing.JobRef}; no new job was enqueued.");
    var recovered = await WaitForAsync(jobs, existing.JobId, static snapshot => snapshot.Status.IsTerminal, "sleeping job to finish");
    EnsureDone(recovered);
    await ShowTimerStateAsync(lab, existing.JobId);
    await host.StopAsync();
    return;
}

var delaySeconds = command == "start" ? 15 : 3;
var job = await jobs.EnqueueAsync(new SendFollowUp("sam@example.com", delaySeconds));
var sleeping = await WaitForAsync(
    jobs,
    job.JobId,
    static snapshot => snapshot.ExecutionNumber >= 1 && snapshot.Status == JobStatusCode.Ready,
    "job to persist its durable timer"
);
await ShowTimerStateAsync(lab, sleeping.JobId);

if (command == "start")
{
    Console.WriteLine();
    Console.WriteLine($"Job {sleeping.JobRef} is sleeping durably. This process will now stop.");
    Console.WriteLine("Inspect and recover that exact identity with:");
    Console.WriteLine($"  dotnet run --project learn/200-durable-execution/205-durable-sleep -- inspect {sleeping.JobRef}");
    Console.WriteLine($"  dotnet run --project learn/200-durable-execution/205-durable-sleep -- recover {sleeping.JobRef}");
    await host.StopAsync();
    return;
}

var terminal = await WaitForAsync(jobs, job.JobId, static snapshot => snapshot.Status.IsTerminal, "sleeping job to finish");
EnsureDone(terminal);
await ShowTimerStateAsync(lab, job.JobId);
await host.StopAsync();

static async Task<GetJobResponse> RequireJobAsync(IJobs jobs, string jobRefText)
{
    JobRef.TryParse(jobRefText, out var jobRef);
    return await jobs.GetAsync(jobRef)
        ?? throw new InvalidOperationException($"Job {jobRefText} was not found. Use the same configured database for every command.");
}

static async Task<GetJobResponse> WaitForAsync(IJobs jobs, long jobId, Func<GetJobResponse, bool> predicate, string description)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
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

static void EnsureDone(GetJobResponse snapshot)
{
    if (snapshot.Status != JobStatusCode.Succeeded)
    {
        throw new InvalidOperationException($"Job {snapshot.JobRef} ended {snapshot.Status}; inspect its events for the reason.");
    }
}

static async Task ShowTimerStateAsync(LearnLab lab, long jobId)
{
    await lab.ShowAllAsync(
        "Explore the complete durable-sleep job record",
        """
        SELECT *
        FROM jobs_view
        WHERE job_id = @jobId
        """,
        new { jobId }
    );
    await lab.ShowAsync(
        "Current durable timer and worker ownership",
        """
        SELECT job_ref, job_status, execution_number, next_run_at_utc, leased_by_worker_id, lease_expires_at_utc
        FROM jobs_view
        WHERE job_id = @jobId
        """,
        new { jobId }
    );
    await lab.ShowAsync(
        "Current durable timer checkpoint",
        """
        SELECT checkpoint_name, checkpoint_kind, checkpoint_status, due_at_utc
        FROM checkpoints_view
        WHERE job_id = @jobId
        """,
        new { jobId }
    );
}

namespace Acta.Learn.DurableSleep
{
    public sealed record SendFollowUp(string UserEmail, int CoolDownSeconds);

    public sealed class FollowUpJob
    {
        [Job("send-follow-up")]
        public async Task Handle(SendFollowUp input, JobContext context, CancellationToken ct)
        {
            Console.WriteLine($"[{input.UserEmail}] handler entered; preserving the cool-down in a durable timer");

            // learn: Sleep by name in the handler
            // `SleepAsync` stores the due time as a row, frees the worker, and ends this execution. When the time comes
            // the handler runs again from its first line, and the sleep, now over, lets it carry on.
            await context.SleepAsync("cool-down", TimeSpan.FromSeconds(input.CoolDownSeconds), ct: ct);
            // learn: end

            // Durable sleep preserves when work becomes due; it does not make a later external delivery exactly once.
            Console.WriteLine($"[{input.UserEmail}] follow-up is due; delivery still needs its own idempotency policy");
        }
    }
}
```

## Notes

### The problem

A process-local delay preserves a continuation only while that process lives. Long delays also tie the
design to one in-memory execution even though no useful work is happening.

### Common approaches

- `Task.Delay` or `PeriodicTimer` inside a live process.
- Self-reschedule and re-check readiness in the handler.
- Persist a named durable timer checkpoint.
- Let an external scheduler enqueue future work.

### Why this design

`SleepAsync` stores the due instant, re-arms the job for the future, clears its worker lease, and ends the
execution. When due, the handler re-enters and the elapsed named timer lets it continue.

### Trade-offs

Re-entry starts at the top; bare code before the sleep runs again. Wake-up precision is bounded by the
database clock and worker polling/wakeup behavior, and every timer adds durable rows and scheduling work.
Code after the sleep can also repeat if the process dies after its external side effect but before job
completion. Durable sleep preserves a due intention; it does not make later delivery exactly once.

### Run the experiment

```bash
dotnet run --project learn/200-durable-execution/205-durable-sleep
```

The lab uses a three-second timer so both phases finish quickly.

### Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

`jobs_view` shows a future `next_run_at_utc` and no lease while `checkpoints_view` carries the timer.
The same job later reaches done with a higher execution number. Views are for operations and learning;
application code should normally use `IJobs`.

### Break it

Run a real process boundary against one configured database:

```bash
dotnet run --project learn/200-durable-execution/205-durable-sleep -- start
dotnet run --project learn/200-durable-execution/205-durable-sleep -- inspect <printed-job-ref>
dotnet run --project learn/200-durable-execution/205-durable-sleep -- recover <printed-job-ref>
```

`start` persists a fifteen-second timer and exits. `inspect` starts no worker and proves the same job is
still stored without a lease. `recover` enqueues nothing: it starts a worker and waits for that exact
identity to re-enter and finish. Contrast this with `Task.Delay`, whose process-local continuation would
have disappeared.

### When not to use

Use `Task.Delay` for short process-local pacing that need not survive restart. Use an external scheduler
when another system owns the timing. Self-reschedule is clearer when each wake must re-check a changing
condition rather than merely wait for an instant.

### Source trail

- [The related Engineering Lab](https://github.com/acta-dotnet/acta/blob/main/docs/engineering-labs.md)
- [`durable-sleep.cs`](https://github.com/acta-dotnet/acta/blob/main/learn/200-durable-execution/205-durable-sleep/durable-sleep.cs)
- [`ArmOrConsumeJobSleepTimer.sqlite.sql`](https://github.com/acta-dotnet/acta/blob/main/src/Acta/Operations/Execution/ArmOrConsumeJobSleepTimer/ArmOrConsumeJobSleepTimer.sqlite.sql)
