# The whole program: wait for an approval without holding a worker

The how-to is [/learn/204-wait-signal.md](/learn/204-wait-signal.md). This file holds every source file of `learn/200-durable-execution/204-wait-signal` as it is compiled, and the notes kept beside the code.

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### wait-signal.cs

```csharp
// How to wait for an approval without holding a worker
// A job parks on a named signal, holds no worker, and carries on when the signal is raised.
using System.Globalization;
using Acta;
using Acta.Labs;
using Acta.Learn.WaitSignal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

const string jobNamespace = "wait-signal";
var commandIndex = Array.FindIndex(args, static arg =>
    arg.Equals("start", StringComparison.OrdinalIgnoreCase)
    || arg.Equals("inspect", StringComparison.OrdinalIgnoreCase)
    || arg.Equals("raise", StringComparison.OrdinalIgnoreCase)
);
var command = commandIndex < 0 ? "run" : args[commandIndex].ToLowerInvariant();
var jobRefIndex = command is "inspect" or "raise" ? commandIndex + 1 : -1;
if (jobRefIndex >= args.Length || (jobRefIndex >= 0 && !JobRef.TryParse(args[jobRefIndex], out _)))
{
    Console.WriteLine($"Usage: dotnet run --project learn/200-durable-execution/204-wait-signal -- {command} <job-ref>");
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
        j.Reference<WaitSignalJobs>(jobNamespace);
    }
    else
    {
        j.Run<WaitSignalJobs>(jobNamespace);
    }
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

if (command == "inspect")
{
    var inspected = await RequireJobAsync(jobs, args[jobRefIndex]);
    await ShowSignalStateAsync(lab, inspected.JobId);
    await host.StopAsync();
    return;
}

if (command == "raise")
{
    var waiting = await RequireJobAsync(jobs, args[jobRefIndex]);
    await ShowSignalStateAsync(lab, waiting.JobId);
    if (waiting.Status != JobStatusCode.Suspended)
    {
        throw new InvalidOperationException($"Job {waiting.JobRef} is {waiting.Status}, not suspended on the approval signal.");
    }

    await jobs.RaiseSignalAsync(JobLookup.ById(waiting.JobId), "approval", new Decision(true, "alice"));
    Console.WriteLine($"Raised approval for the existing durable identity {waiting.JobRef}.");
    var completed = await WaitForAsync(jobs, waiting.JobId, static snapshot => snapshot.Status.IsTerminal, "signal-waiting job to finish");
    EnsureDone(completed);
    await ShowSignalStateAsync(lab, waiting.JobId);
    await host.StopAsync();
    return;
}

var job = await jobs.EnqueueAsync(new ApproveExpense("exp-1", 4_200m));
var suspended = await WaitForAsync(
    jobs,
    job.JobId,
    static snapshot => snapshot.Status == JobStatusCode.Suspended,
    "job to suspend on approval"
);
await ShowSignalStateAsync(lab, suspended.JobId);

if (command == "start")
{
    Console.WriteLine();
    Console.WriteLine($"Job {suspended.JobRef} is durably suspended. This process will now stop.");
    Console.WriteLine("Inspect and resume that exact identity with:");
    Console.WriteLine($"  dotnet run --project learn/200-durable-execution/204-wait-signal -- inspect {suspended.JobRef}");
    Console.WriteLine($"  dotnet run --project learn/200-durable-execution/204-wait-signal -- raise {suspended.JobRef}");
    await host.StopAsync();
    return;
}

if (!lab.Brief && !Console.IsInputRedirected)
{
    Console.WriteLine("Press S to approve the expense...");
    while (Console.ReadKey(intercept: true).Key != ConsoleKey.S) { }
}
else
{
    Console.WriteLine("Brief/non-interactive run: raising the approval signal automatically.");
}

// learn: 2. Raise the signal when the answer arrives
// Whoever learns the answer raises the signal by name on that job: an endpoint, another job, an operator. The value
// is stored, the job becomes ready, and the handler runs again from its first line. This time the wait returns the
// stored value.
await jobs.RaiseSignalAsync(job, "approval", new Decision(true, "alice"));

// learn: end
Console.WriteLine("approved - the job resumes.");
var terminal = await WaitForAsync(jobs, job.JobId, static snapshot => snapshot.Status.IsTerminal, "signal-waiting job to finish");
EnsureDone(terminal);
await ShowSignalStateAsync(lab, job.JobId);
await host.StopAsync();

static async Task<GetJobResponse> RequireJobAsync(IJobs jobs, string jobRefText)
{
    JobRef.TryParse(jobRefText, out var jobRef);
    return await jobs.GetAsync(jobRef)
        ?? throw new InvalidOperationException($"Job {jobRefText} was not found. Use the same configured database for every command.");
}

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

static void EnsureDone(GetJobResponse snapshot)
{
    if (snapshot.Status != JobStatusCode.Succeeded)
    {
        throw new InvalidOperationException($"Job {snapshot.JobRef} ended {snapshot.Status}; inspect its events for the reason.");
    }
}

static async Task ShowSignalStateAsync(LearnLab lab, long jobId)
{
    await lab.ShowAllAsync(
        "Explore the complete signal-waiting job record",
        """
        SELECT *
        FROM jobs_view
        WHERE job_id = @jobId
        """,
        new { jobId }
    );
    await lab.ShowAsync(
        "Current job state and worker ownership",
        """
        SELECT job_ref, job_status, execution_number, leased_by_worker_id, lease_expires_at_utc
        FROM jobs_view
        WHERE job_id = @jobId
        """,
        new { jobId }
    );
    await lab.ShowAsync(
        "Current durable approval checkpoint",
        """
        SELECT checkpoint_name, checkpoint_kind, checkpoint_status, value_format
        FROM checkpoints_view
        WHERE job_id = @jobId
        """,
        new { jobId }
    );
}

namespace Acta.Learn.WaitSignal
{
    public sealed record ApproveExpense(string Id, decimal Amount);

    public sealed record Decision(bool Approved, string By);

    public sealed class ApproveExpenseJob
    {
        [Job("approve-expense")]
        public async Task Handle(ApproveExpense expense, JobContext context, CancellationToken ct)
        {
            // learn: 1. Wait for a named signal in the handler
            // `WaitSignalAsync` stores the wait as a row and ends this execution. The job reads `suspended`.
            // Suspends (no executor held) until the named signal is raised, then re-enters with the latched payload.
            var decision = await context.WaitSignalAsync<Decision>("approval", ct);
            // learn: end
            var amount = expense.Amount.ToString("0.00", CultureInfo.InvariantCulture);
            Console.WriteLine($"[{expense.Id}] expense for {amount} EUR {(decision!.Approved ? "APPROVED" : "rejected")} by {decision.By}");
        }
    }
}
```

## Notes

### The problem

A job may wait hours or days for a person or another system. Keeping a task alive consumes capacity and
still loses the continuation when the process stops.

### Common approaches

- Block a task and hold process-local state.
- Poll until the external condition becomes true.
- Self-reschedule when the job can check readiness itself.
- Latch an explicit signal when another actor knows the event happened.
- Delegate a large externally orchestrated process to a workflow engine.

### Why this design

`WaitSignalAsync` writes a named checkpoint and ends the execution. The worker lease is cleared. Raising
the signal stores its payload and re-arms the same job; the handler re-enters and reads the latch, which stays set for any later replay.

### Trade-offs

The handler starts from the top on execution two. Only durable steps and checkpoints suppress repeated
work. Signals also require a reliable actor to raise the correct named latch and a policy for signals
that never arrive.

### Run the experiment

```bash
dotnet run --project learn/200-durable-execution/204-wait-signal
```

Press `S` when prompted. Use `--brief` for an automatic non-interactive run.

### Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

Before the signal, `jobs_view` shows `suspended` with null worker/lease columns and
`checkpoints_view` shows a pending signal. After the raise, the checkpoint contains the latched value
and the same job finishes on execution two.

### Break it

Run a real two-process recovery against one configured database:

```bash
dotnet run --project learn/200-durable-execution/204-wait-signal -- start
dotnet run --project learn/200-durable-execution/204-wait-signal -- inspect <printed-job-ref>
dotnet run --project learn/200-durable-execution/204-wait-signal -- raise <printed-job-ref>
```

`start` stops after suspension. `inspect` starts no worker and proves that the exact identity remains
suspended and unleased. `raise` does not enqueue another job; it signals that identity and waits for its
second execution to finish.

### When not to use

Use rescheduling instead when the job owns the readiness check. Use polling when the source cannot emit
a signal and the load is acceptable. Prefer a workflow product for very large, externally authored,
human-centric processes.

### Source trail

- [The related Engineering Lab](https://github.com/acta-dotnet/acta/blob/main/docs/engineering-labs.md)
- [`wait-signal.cs`](https://github.com/acta-dotnet/acta/blob/main/learn/200-durable-execution/204-wait-signal/wait-signal.cs)
- [`RuntimeJobContext.cs`](https://github.com/acta-dotnet/acta/blob/main/src/Acta/Engine/Worker/Context/RuntimeJobContext.cs)
- [`WaitJobSignal.sqlite.sql`](https://github.com/acta-dotnet/acta/blob/main/src/Acta/Operations/Signals/WaitJobSignal/WaitJobSignal.sqlite.sql)
