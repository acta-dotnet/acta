# The whole program: refuse to repeat a side effect after a crash

The how-to is [/learn/220-at-most-once-step.md](/learn/220-at-most-once-step.md). This file holds every source file of `learn/200-durable-execution/220-at-most-once-step` as it is compiled, and the notes kept beside the code.

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on any of the three databases and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### at-most-once-step.cs

```csharp
// How to refuse to repeat a side effect after a crash
// An at-most-once step refuses to run its body a second time after a crash. The handler is told that the outcome is
// unknown, and reconciles.
using Acta;
using Acta.Labs;
using Acta.Learn.AtMostOnceStep;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

const string jobNamespace = "at-most-once-step";
var currentJobFile = Path.Combine(Path.GetTempPath(), "acta-at-most-once-current-job.txt");

var mode = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))?.ToLowerInvariant();
if (mode is not ("crash" or "recover"))
{
    Console.WriteLine("Run this two-process experiment in order:");
    Console.WriteLine("  dotnet run --project learn/200-durable-execution/220-at-most-once-step -- crash");
    Console.WriteLine("  dotnet run --project learn/200-durable-execution/220-at-most-once-step -- recover");
    return;
}

// Keep provider/configuration switches, but do not pass the bare mode to .NET's configuration parser.
var hostArgs = args.Where(a =>
        !string.Equals(a, mode, StringComparison.OrdinalIgnoreCase) && a is not "--brief" and not "--pause" and not "--all-columns"
    )
    .ToArray();
var builder = Host.CreateApplicationBuilder(hostArgs);
var lab = new LearnLab(builder.Configuration, args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<AtMostOnceStepJobs>(jobNamespace);
    j.ConfigureOptions(o =>
    {
        // Lab-only timing: production should keep the documented, generous heartbeat margin. The
        // heartbeat is the only knob, and the lease (4s) and dead-worker window (7s) follow from it,
        // so a demo cannot land on a combination that reclaims a live worker's own jobs.
        o.HeartbeatInterval = TimeSpan.FromSeconds(1);
    });
});

using var host = builder.Build();
await host.StartAsync();
var jobs = host.Services.GetRequiredService<IJobs>();

if (mode == "crash")
{
    var runId = Guid.CreateVersion7().ToString("N");
    var enqueued = await jobs.EnqueueAsync(new ChargeCard($"order-{runId}", 125_00), o =>
        o.DeduplicationKey(DeduplicationKey.ForDefinition("charge-card", $"charge-card-lab-{runId}")).Delayed(TimeSpan.FromSeconds(2))
    );
    File.WriteAllText(currentJobFile, enqueued.JobRef.ToString());
    Console.WriteLine($"Enqueued {enqueued.JobRef}. The process will terminate inside the at-most-once step.");
    Console.WriteLine("A non-zero exit is expected. Then run the recover command against the same database.");
    await host.WaitForShutdownAsync();
    return;
}

if (!File.Exists(currentJobFile) || !JobRef.TryParse(File.ReadAllText(currentJobFile).Trim(), out var currentJobRef))
{
    Console.WriteLine("No current crashed-job marker exists. Run the crash command first.");
    await host.StopAsync();
    return;
}
var crashed = await jobs.GetAsync(currentJobRef);
if (crashed is null)
{
    Console.WriteLine(
        $"The marker names {currentJobRef}, but that job is not in this database. Run crash and recover with the same provider."
    );
    await host.StopAsync();
    return;
}

await lab.ShowAllAsync(
    "Explore the complete job record left by the crashed process",
    """
    SELECT *
    FROM jobs_view
    WHERE job_id = @jobId
    """,
    new { jobId = crashed.JobId }
);
await lab.ShowAsync(
    "After the process loss: execution ownership remains, and the step outcome is pending",
    """
    SELECT job_ref, job_status, execution_number, leased_by_worker_id, lease_expires_at_utc
    FROM jobs_view
    WHERE job_id = @jobId
    """,
    new { jobId = crashed.JobId }
);
await lab.ShowAsync(
    "The durable start exists, but no completion was recorded",
    """
    SELECT step_name, step_status, failure_message
    FROM steps_view
    WHERE job_id = @jobId
    """,
    new { jobId = crashed.JobId }
);

Console.WriteLine(
    "Waiting for recovery: on SQLite this host's start releases the crashed process's job at once; on a server database the heartbeat takes the pass once the short lab lease lapses..."
);

using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
while (true)
{
    var snapshot = await jobs.GetAsync(JobLookup.ById(crashed.JobId), recoveryTimeout.Token);
    if (snapshot?.Status.IsTerminal == true)
    {
        break;
    }
    await Task.Delay(100, recoveryTimeout.Token);
}

await lab.ShowAsync(
    "On replay Acta refuses a second body invocation",
    """
    SELECT step_name, step_status, failure_message
    FROM steps_view
    WHERE job_id = @jobId
    """,
    new { jobId = crashed.JobId }
);
await lab.ShowAsync(
    "Recovery and reconciliation remain in the event ledger",
    """
    SELECT event, execution_number, event_message, details_text
    FROM events_view
    WHERE entity = 'job' AND entity_id = @jobId
    ORDER BY event_id
    """,
    new { jobId = crashed.JobId }
);

var sideEffectFile = ChargeCardJob.SideEffectFile(crashed.JobRef);
var sideEffects = File.Exists(sideEffectFile) ? File.ReadAllLines(sideEffectFile).Length : 0;
Console.WriteLine($"External side-effect records: {sideEffects} (the charge body was not invoked again).");
await host.StopAsync();

namespace Acta.Learn.AtMostOnceStep
{
    public sealed record ChargeCard(string OrderId, int AmountCents);

    public static class ChargeCardJob
    {
        public static string SideEffectFile(JobRef jobRef) =>
            Path.Combine(Path.GetTempPath(), $"acta-at-most-once-side-effects-{jobRef}.log");

        [Job("charge-card", MaxAttempts = 3, Backoff = "0s")]
        public static async Task Handle(ChargeCard input, JobContext context, CancellationToken ct)
        {
            try
            {
                await context.RunStepAsync(
                    "charge-card",
                    _ =>
                    {
                        // Simulated external system: this write cannot share Acta's SQL transaction.
                        File.AppendAllText(SideEffectFile(context.JobRef), $"{input.OrderId}:{input.AmountCents}{Environment.NewLine}");
                        Console.WriteLine("SIDE EFFECT COMMITTED. Terminating before Acta can record the step outcome...");
                        Console.Out.Flush();
                        Environment.FailFast("Intentional at-most-once lab crash after the external side effect.");
                        return Task.CompletedTask;
                    },
                    // learn: 1. Mark the step at-most-once
                    // The step's start is recorded before its body runs. When the process dies inside the body, the
                    // replay does not call it again: the step throws `StepInterruptedException` instead.
                    options => options.AtMostOnce(),
                    // learn: end
                    ct
                );
            }
            // learn: 2. Catch the interruption and reconcile
            // The side effect may or may not have happened. Ask the outside system, then decide.
            catch (StepInterruptedException interrupted)
            {
                var sideEffectFile = SideEffectFile(context.JobRef);
                var matchingCharges = File.Exists(sideEffectFile)
                    ? File.ReadLines(sideEffectFile).Count(line => line.StartsWith(input.OrderId + ":", StringComparison.Ordinal))
                    : 0;
                Console.WriteLine($"{interrupted.StepName} is ambiguous; reconciliation found {matchingCharges} external charge(s).");
                Console.WriteLine("The handler owns this policy: compensate, confirm, alert, or continue.");
            }
            // learn: end
        }
    }
}
```

## Notes

### The problem

No local transaction can atomically commit both a remote charge and Acta's step outcome. If the process
dies in that gap, retrying may duplicate the charge; refusing retry leaves an unknown outcome.

### Common approaches

- Accept at-least-once execution and make the side effect idempotent.
- Send an external deduplication key and query the external result.
- Refuse a second invocation with an at-most-once step.
- Reconcile an ambiguous outcome and compensate or continue.

### Why this design

`AtMostOnce()` durably marks the step start before invoking the body. Re-entering a still-pending step
terminalizes it as `interrupted` and throws `StepInterruptedException`; Acta never calls that body a
second time. The handler then owns reconciliation policy. A body that learns of the same ambiguity without
dying, a timeout after the request left, throws `OutcomeUnknownException` and ends the step the same way.

### Trade-offs

At-most-once exchanges possible duplication for possible omission/ambiguity. It does not prove that the
side effect happened. That is why external idempotency plus a readable result is usually safer.

### Run the experiment

Use the same configured database for both commands:

```bash
dotnet run --project learn/200-durable-execution/220-at-most-once-step -- crash
dotnet run --project learn/200-durable-execution/220-at-most-once-step -- recover
```

The first command intentionally exits non-zero after writing a simulated external charge. It creates a
fresh durable identity and records that ref in a local current-run marker, so the two-command experiment
can be repeated against the same database. The simulated external side-effect file is named by job ref,
so evidence from another lab identity cannot be erased or counted as part of this one.

### Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

Before recovery, `jobs_view` is executing with a lapsed owner and `steps_view` is pending. After crash
recovery re-arms it, the step becomes `interrupted`, and its `failure_message` says why;
`events_view` preserves the loss and replay. On SQLite the restarted host releases the crashed
process's job at once; on a server database the lab's short lease timings make the wait quick.

### Break it

Comment out `AtMostOnce()` and repeat: the body can run again after recovery. Then
replace the file with a fake external API that accepts a deduplication key and compare the certainty it
can provide.

### When not to use

Do not choose at-most-once merely to avoid thinking about idempotency. Use it only when duplicate work is
worse than missing/ambiguous work and a reconciliation path exists.

### Source trail

- [The related Engineering Lab](https://github.com/acta-dotnet/acta/blob/main/docs/engineering-labs.md)
- [`at-most-once-step.cs`](https://github.com/acta-dotnet/acta/blob/main/learn/200-durable-execution/220-at-most-once-step/at-most-once-step.cs)
- [`RuntimeJobContext.cs`](https://github.com/acta-dotnet/acta/blob/main/src/Acta/Engine/Worker/Context/RuntimeJobContext.cs)
- [`StepAtMostOnceSpec.cs`](https://github.com/acta-dotnet/acta/blob/main/tests/Acta.Tests.Conformance/Scenarios/StepAtMostOnceSpec.cs)
