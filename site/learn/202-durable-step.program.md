# The whole program: keep finished work from repeating on a retry

The how-to is [/learn/202-durable-step.md](/learn/202-durable-step.md). This file holds every source file of `learn/200-durable-execution/202-durable-step` as it is compiled, and the notes kept beside the code.

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### durable-step.cs

```csharp
// How to keep finished work from repeating on a retry
// A named step records its outcome, so a retry returns it instead of doing the work again.
using Acta;
using Acta.Labs;
using Acta.Learn.DurableStep;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
var lab = new LearnLab(builder.Configuration, args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<DurableStepJobs>("durable-step");
});

// Handler fails once to show retry; quiet the framework failure warning/alert.
builder.Logging.AddFilter("Acta", LogLevel.Error);

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();
var enqueued = await jobs.EnqueueAsync(new FreezeBox("box-1"));

GetJobResponse? snapshot;
using var completionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
do
{
    await Task.Delay(100, completionTimeout.Token);
    snapshot = await jobs.GetAsync(enqueued, completionTimeout.Token);
} while (snapshot is null or { Status.IsFinished: false });

await lab.ShowAllAsync(
    "Explore the complete replayed job record",
    """
    SELECT *
    FROM jobs_view
    WHERE job_id = @jobId
    """,
    new { jobId = enqueued.JobId }
);
await lab.ShowAsync(
    "One job replayed; the recorded step outcome was reused",
    """
    SELECT job_ref, job_status, execution_number, failure_count
    FROM jobs_view
    WHERE job_id = @jobId
    """,
    new { jobId = enqueued.JobId }
);
await lab.ShowAsync(
    "The durable step stores the completed outcome",
    """
    SELECT step_name, step_status, failure_message
    FROM steps_view
    WHERE job_id = @jobId
    """,
    new { jobId = enqueued.JobId }
);
await lab.ShowAsync(
    "The event ledger keeps both executions",
    """
    SELECT event, execution_number, event_message, details_text
    FROM events_view
    WHERE entity = 'job' AND entity_id = @jobId
    ORDER BY event_id
    """,
    new { jobId = enqueued.JobId }
);

await host.StopAsync();

namespace Acta.Learn.DurableStep
{
    public sealed record FreezeBox(string BoxId);

    public sealed class FreezeBoxJob
    {
        private static int _attempts;

        // MaxAttempts = 2: first attempt fails, second succeeds; retry waits 2s.
        [Job("freeze-box", MaxAttempts = 2, Backoff = "2s")]
        public async Task Handle(FreezeBox box, JobContext context, CancellationToken ct)
        {
            // learn: 1. Put the work in a named step
            // `RunStepAsync` gives the work a name and records its outcome under that name. On a later run of the
            // handler, a step with a recorded outcome returns it instead of running its body.
            // In this controlled path the later failure happens after step success is durable, so execution two reuses that outcome.
            // A process crash after the external request but before Acta records success could invoke a normal step body again.
            await context.RunStepAsync(
                "start-freeze-cycle",
                _ =>
                {
                    Console.WriteLine($"[{box.BoxId}] requesting START-FREEZE with stable deduplication key freeze:{box.BoxId}");
                    return Task.CompletedTask;
                },
                ct
            );
            // learn: end

            // learn: 2. Fail once after the step, and watch the retry skip it
            // Fail the first attempt; the retry skips the already-started freeze cycle.
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                Console.WriteLine($"[{box.BoxId}] thermostat hiccup - retrying...");
                throw new InvalidOperationException("simulated transient failure");
            }
            // learn: end

            Console.WriteLine($"[{box.BoxId}] box frozen");
        }
    }
}
```

## Notes

### The problem

A retry repeats a handler. If an earlier side effect succeeded, repeating it can double-charge, send
twice, or corrupt an external state machine.

### Common approaches

| Approach | Benefit | Cost |
| --- | --- | --- |
| Bare handler code | Minimal ceremony | Repeats on replay |
| Normal durable step | Replays a recorded success | A crash before recording success can repeat the body |
| External deduplication key | Usually the strongest answer | The external system must implement it |
| Step with a check | Asks the external system before repeating a started step | The external system must be able to answer, and can answer late |
| At-most-once step | Acta will not invoke an interrupted body again | The outcome can remain ambiguous |
| Reconciliation | Resolves ambiguity from external evidence | Requires a readable external state/API |

### Why this design

`RunStepAsync` gives a named operation a durable record. Once the outcome is recorded, later executions
return it instead of calling the body again. The built-in failure after the step proves that distinction.

### Trade-offs

A normal step closes the replay gap only after completion is durable. It cannot atomically commit an
external side effect and Acta's row. For the interrupted-at-most-once choice, continue with
[`220-at-most-once-step`](/learn/220-at-most-once-step.md) rather than mistaking this lab for exactly-once.

### Run the experiment

```bash
dotnet run --project learn/200-durable-execution/202-durable-step
```

The handler fails once after `start-freeze-cycle`; the job reaches execution two, while this controlled
failure-after-completion path invokes the body once and the step row stays `succeeded`. The stable
external deduplication key in the example is still necessary for the crash-before-recording window.

### Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

The lab prints `jobs_view`, `steps_view`, and `events_view`. The job row proves replay; the step row
proves reuse of a recorded outcome; the event rows explain why execution one re-armed.

### Break it

Move the simulated failure into the step body and compare the row. The step stays `pending`, the throw
fails the attempt, and the job's retry runs the body again under the job's `MaxAttempts`. Then use lab 220
to explore a process stopping after step start but before outcome recording.

### When not to use

Do not use a durable step to disguise a non-idempotent API when that API offers a real deduplication key.
Prefer the external guarantee. Do not create a step for pure, cheap computation unless replay cost or
determinism makes the stored result worthwhile.

### Source trail

- [The related Engineering Lab](https://github.com/acta-dotnet/acta/blob/main/docs/engineering-labs.md)
- [`durable-step.cs`](https://github.com/acta-dotnet/acta/blob/main/learn/200-durable-execution/202-durable-step/durable-step.cs)
- [`RuntimeJobContext.cs`](https://github.com/acta-dotnet/acta/blob/main/src/Acta/Engine/Worker/Context/RuntimeJobContext.cs)
- [`StepAtMostOnceSpec.cs`](https://github.com/acta-dotnet/acta/blob/main/tests/Acta.Tests.Conformance/Scenarios/StepAtMostOnceSpec.cs)
