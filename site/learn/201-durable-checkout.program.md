# The whole program: build a checkout that waits for a review and a timer

The how-to is [/learn/201-durable-checkout.md](/learn/201-durable-checkout.md). This file holds every source file of `learn/200-durable-execution/201-durable-checkout` as it is compiled, and the notes kept beside the code.

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### Program.cs

```csharp
// How to build a checkout that waits for a review and a timer
// One handler reads top to bottom as a checkout: wait for a fraud review, reserve, charge, wait out a settlement
// window, send the receipt. Each stage is a signal, a step, a variable, or a timer that survives a restart.
using Acta;
using Acta.Labs;
using Acta.Learn.DurableCheckout;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var runId = Guid.NewGuid().ToString("N")[..16];
var scenario = new CheckoutLabScenario(
    Namespace: $"checkout-lab-{runId}",
    OrderId: $"order-{runId}",
    RejectFraudReview: args.Contains("--reject", StringComparer.OrdinalIgnoreCase)
);

builder.Services.AddSingleton(scenario);
builder.Services.AddSingleton(new LearnLab(builder.Configuration, args));

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<DurableCheckoutJobs>(scenario.Namespace);
});

builder.Services.AddHostedService<Primer>();

await builder.Build().RunAsync();
```

### Checkout.cs

```csharp
namespace Acta.Learn.DurableCheckout;

public sealed record Checkout(string OrderId, decimal Amount);

public sealed record ChargeResult(string ChargeId);

public sealed record FraudDecision(bool Approved, string Reviewer);

public sealed record CheckoutResult(string OrderId, string ChargeId);

public sealed class CheckoutJob
{
    [Job("checkout")]
    public async Task<CheckoutResult> Handle(Checkout order, JobContext context, CancellationToken ct)
    {
        // The review comes first, so a rejected order has reserved and charged nothing and needs no refund.
        if (order.Amount >= 500m)
        {
            // learn: 1. Wait for the review before anything is charged
            // No thread or executor waits here. Acta stores the signal checkpoint, releases the lease,
            // and re-enters this handler from the top after another actor raises the signal. `CancelAsync`
            // ends the handler there, so the lines after it run only for an approved order.
            var decision = await context.WaitSignalAsync<FraudDecision>("fraud-review", ct);
            if (decision is not { Approved: true })
            {
                await context.CancelAsync($"fraud review rejected by {decision?.Reviewer}", ct);
            }
            // learn: end

            // Deliberately ordinary, repeat-safe diagnostic code: it runs after the signal replay and
            // again after the timer replay. External side effects belong in idempotent durable steps.
            Console.WriteLine($"[{order.OrderId}] approval observed by handler; reviewer={decision!.Reviewer}");
        }

        // learn: 2. Put each outside call in a step
        // Normal durable steps replay completed outcomes, but an in-flight body can run again after a
        // crash. These simulated external calls therefore carry stable deduplication keys. A real
        // inventory, payment, or mail service must persist and honor the same contract.
        await context.RunStepAsync(
            "reserve-inventory",
            async innerCt =>
            {
                await Task.Delay(150, innerCt);
                Console.WriteLine($"[{order.OrderId}] inventory request: deduplication-key=reservation:{order.OrderId}");
            },
            ct
        );

        var charge = await context.RunStepAsync(
            "charge-card",
            async innerCt =>
            {
                await Task.Delay(150, innerCt);
                Console.WriteLine($"[{order.OrderId}] payment request: deduplication-key=payment:{order.OrderId}");
                return new ChargeResult($"ch_{order.OrderId}");
            },
            ct
        );
        // learn: end

        // learn: 3. Keep a value in a variable
        // Get-or-set demonstrates that the variable row is read, not rewritten, on handler re-entry.
        var chargeId = await context.GetOrSetVariableAsync("charge-id", () => charge.ChargeId, ct);
        // learn: end

        // learn: 4. Wait out the settlement window with a named timer
        // The named timer stores an intention in SQL. It does not preserve a process-local continuation.
        await context.SleepAsync("settlement-delay", TimeSpan.FromSeconds(5), "simulated settlement window", ct);
        // learn: end

        await context.RunStepAsync(
            "send-receipt",
            async innerCt =>
            {
                var storedChargeId = await context.GetVariableAsync<string>("charge-id", innerCt);
                await Task.Delay(100, innerCt);
                Console.WriteLine($"[{order.OrderId}] receipt request for {storedChargeId}: deduplication-key=receipt:{order.OrderId}");
            },
            ct
        );

        return new CheckoutResult(order.OrderId, chargeId);
    }
}
```

### Primer.cs

```csharp
using Acta.Labs;
using Microsoft.Extensions.Hosting;

namespace Acta.Learn.DurableCheckout;

internal sealed record CheckoutLabScenario(string Namespace, string OrderId, bool RejectFraudReview);

internal sealed class Primer(IJobs jobs, LearnLab lab, CheckoutLabScenario scenario, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Console.WriteLine($"HERO 201 run: namespace={scenario.Namespace}, order={scenario.OrderId}");

        // Hold the job in the future so the first snapshot is deterministic on every provider. Releasing
        // it through IJobs below is an audited operator action; no worker can race the enqueue snapshot.
        var checkout = await jobs.EnqueueAsync(
            new Checkout(scenario.OrderId, 2_499m),
            options => options.Delayed(TimeSpan.FromHours(1)),
            ct
        );

        await lab.ShowAsync(
            "1. Enqueued - stable identity exists before the first execution",
            """
            SELECT job_ref, namespace_name, job_status, execution_number, next_run_at_utc, leased_by_worker_id, lease_expires_at_utc
            FROM jobs_view
            WHERE job_id = @jobId
            """,
            new { jobId = checkout.JobId },
            ct
        );
        await ShowSplitStateAsync("The append-mostly job row and hot jobs_state row start together", checkout.JobId, ct);

        var release = await jobs.RescheduleAsync(
            checkout,
            DateTime.UnixEpoch,
            "release the HERO 201 enqueue snapshot",
            "how-to-201",
            ct: ct
        );
        if (release.Action != ControlAction.Applied)
        {
            throw new InvalidOperationException($"Could not release checkout {checkout.JobRef}: {release.Action}.");
        }

        await WaitForAsync(
            checkout.JobId,
            snapshot => snapshot.Status == JobStatusCode.Suspended && snapshot.ExecutionNumber == 1,
            "fraud-review suspension",
            ct
        );

        await lab.ShowAllAsync(
            "Explore - the complete checkout record at the fraud-review boundary",
            """
            SELECT *
            FROM jobs_view
            WHERE job_id = @jobId
            """,
            new { jobId = checkout.JobId },
            ct
        );
        await lab.ShowAsync(
            "2. Fraud wait - the job is suspended and owns no worker",
            """
            SELECT job_ref, job_status, execution_number, leased_by_worker_id, lease_expires_at_utc
            FROM jobs_view
            WHERE job_id = @jobId
            """,
            new { jobId = checkout.JobId },
            ct
        );
        await lab.ShowAsync(
            "Nothing is reserved or charged before the review: no step has run",
            """
            SELECT step_name, step_status, result_format
            FROM steps_view
            WHERE job_id = @jobId
            ORDER BY created_at_utc
            """,
            new { jobId = checkout.JobId },
            ct
        );
        await lab.ShowAsync(
            "The pending approval is a checkpoint row",
            """
            SELECT checkpoint_name, checkpoint_kind, checkpoint_status, value_format, due_at_utc, modified_at_utc
            FROM checkpoints_view
            WHERE job_id = @jobId
            ORDER BY checkpoint_name
            """,
            new { jobId = checkout.JobId },
            ct
        );

        var decision = new FraudDecision(!scenario.RejectFraudReview, "alice");
        await jobs.RaiseSignalAsync(checkout, "fraud-review", decision, ct);
        Console.WriteLine($"[{scenario.OrderId}] fraud-review raised: {(decision.Approved ? "approved" : "rejected")}");

        if (scenario.RejectFraudReview)
        {
            await WaitForAsync(checkout.JobId, snapshot => snapshot.Status == JobStatusCode.Cancelled, "fraud rejection cancellation", ct);
            await lab.ShowAsync(
                "3. Rejected - cancellation is terminal and no timer or receipt was created",
                """
                SELECT job_ref, job_status, execution_number, failure_count, result_format
                FROM jobs_view
                WHERE job_id = @jobId
                """,
                new { jobId = checkout.JobId },
                ct
            );
            await lab.ShowAsync(
                "No step ran: inventory and payment come after the review",
                """
                SELECT step_name, step_status, result_format
                FROM steps_view
                WHERE job_id = @jobId
                ORDER BY created_at_utc
                """,
                new { jobId = checkout.JobId },
                ct
            );
            await lab.ShowAsync(
                "The rejected signal is latched, but no timer checkpoint exists",
                """
                SELECT checkpoint_name, checkpoint_kind, checkpoint_status, value_format, due_at_utc
                FROM checkpoints_view
                WHERE job_id = @jobId
                ORDER BY checkpoint_name
                """,
                new { jobId = checkout.JobId },
                ct
            );
            await lab.ShowAsync(
                "Cancellation stores no result on the jobs_state row",
                """
                SELECT job_id, execution_number, result_format_id
                FROM {{schema}}.jobs_state
                WHERE job_id = @jobId
                """,
                new { jobId = checkout.JobId },
                ct
            );
            await ShowLedgerAsync(checkout.JobId, ct);
            lifetime.StopApplication();
            return;
        }

        await WaitForAsync(
            checkout.JobId,
            snapshot => snapshot.Status == JobStatusCode.Ready && snapshot.ExecutionNumber == 2,
            "settlement timer",
            ct
        );
        await lab.ShowAsync(
            "3. Timer wait - the next intention is durable and the worker lease is released",
            """
            SELECT job_ref, job_status, execution_number, next_run_at_utc, leased_by_worker_id, lease_expires_at_utc
            FROM jobs_view
            WHERE job_id = @jobId
            """,
            new { jobId = checkout.JobId },
            ct
        );
        await lab.ShowAsync(
            "The signal is latched, the charge is a variable, and the settlement timer is pending",
            """
            SELECT checkpoint_name, checkpoint_kind, checkpoint_status, due_at_utc, modified_at_utc
            FROM checkpoints_view
            WHERE job_id = @jobId
            ORDER BY checkpoint_name
            """,
            new { jobId = checkout.JobId },
            ct
        );

        await WaitForAsync(checkout.JobId, snapshot => snapshot.Status == JobStatusCode.Succeeded, "terminal completion", ct);
        await lab.ShowAsync(
            "4. Done - one identity completed through three handler entries",
            """
            SELECT job_ref, job_status, execution_number, failure_count, result_format, leased_by_worker_id
            FROM jobs_view
            WHERE job_id = @jobId
            """,
            new { jobId = checkout.JobId },
            ct
        );
        await lab.ShowAsync(
            "All external operations have one successful durable outcome",
            """
            SELECT step_name, step_status, result_format
            FROM steps_view
            WHERE job_id = @jobId
            ORDER BY created_at_utc
            """,
            new { jobId = checkout.JobId },
            ct
        );
        await lab.ShowAsync(
            "The variable survived, the signal stayed latched, and the timer was consumed",
            """
            SELECT checkpoint_name, checkpoint_kind, checkpoint_status, value_format, due_at_utc, modified_at_utc
            FROM checkpoints_view
            WHERE job_id = @jobId
            ORDER BY checkpoint_name
            """,
            new { jobId = checkout.JobId },
            ct
        );
        await lab.ShowAsync(
            "The terminal result is stored on the jobs_state row, beside the execution that produced it",
            """
            SELECT job_id, execution_number, result_format_id
            FROM {{schema}}.jobs_state
            WHERE job_id = @jobId
            """,
            new { jobId = checkout.JobId },
            ct
        );
        await ShowSplitStateAsync("The stable job row stayed put while the jobs_state row advanced", checkout.JobId, ct);
        await ShowLedgerAsync(checkout.JobId, ct);

        lifetime.StopApplication();
    }

    private async Task<GetJobResponse> WaitForAsync(
        long jobId,
        Func<GetJobResponse, bool> predicate,
        string description,
        CancellationToken ct
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            while (true)
            {
                var snapshot = await jobs.GetAsync(JobLookup.ById(jobId), timeout.Token);
                if (snapshot is not null && predicate(snapshot))
                {
                    return snapshot;
                }
                await Task.Delay(50, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for {description} on job {jobId}.");
        }
    }

    private Task ShowSplitStateAsync(string title, long jobId, CancellationToken ct) =>
        lab.ShowAsync(
            title,
            """
            SELECT j.job_id, j.input_format_id AS stable_input_format, j.created_at_utc AS stable_created_at_utc, r.job_status_code AS state_status_code, r.execution_number AS state_executions, r.failure_count AS state_failures, r.leased_by_worker_id AS state_worker, r.modified_at_utc AS state_modified_at_utc
            FROM {{schema}}.jobs AS j
            INNER JOIN {{schema}}.jobs_state AS r ON r.job_id = j.job_id
            WHERE j.job_id = @jobId
            """,
            new { jobId },
            ct
        );

    private Task ShowLedgerAsync(long jobId, CancellationToken ct) =>
        lab.ShowAsync(
            "The append-only event ledger explains every transition",
            """
            SELECT event, execution_number, event_message, details_text
            FROM events_view
            WHERE entity = 'job' AND entity_id = @jobId
            ORDER BY event_id
            """,
            new { jobId },
            ct
        );
}
```

## Notes

### The problem

A checkout crosses inventory, payment, human review, a time boundary, and notification. A process can
stop between any two instructions. Retrying a handler does not by itself say which work may repeat,
what the checkout is waiting for, or how an operator can prove what happened.

### Common approaches

- Keep the continuation in memory and lose it when the process stops.
- Put flags in an application table and build a state-machine runner around them.
- Replay everything and require every external system to honor a deduplication key.
- Adopt a workflow engine with a separate state model and operating surface.
- Use a distributed transaction where every participant genuinely supports one.

### Why this design

Acta gives one checkout identity several explicit relational facts: completed step outcomes, a durable
variable, a signal latch, a timer, a terminal result, and an append-only event ledger. The worker uses
those same facts to execute the job, while operators use curated views to understand it.

The job and job-state rows are deliberately split. Stable identity and input stay on `jobs`; frequently
updated status, scheduling, counters, and worker ownership live on the smaller `jobs_state` row.

### Trade-offs

The handler re-enters from the top after each durable wait. Completed steps replay their stored outcome,
but a normal step body can still run again if the process dies after its external side effect and before
Acta records completion. The example therefore passes stable deduplication keys to its simulated external
systems. A real inventory, payment, or mail API must persist and honor those keys.

Every named step and checkpoint is also a durable contract and an additional row. This is more explicit
and inspectable than a process-local continuation, but it is not free and it is not exactly-once execution.

### Run the experiment

```bash
dotnet run --project learn/200-durable-execution/201-durable-checkout
```

The program creates one uniquely scoped checkout, holds it long enough to inspect the enqueue state,
then walks automatically through four snapshots:

1. Enqueued but not yet executed.
2. Suspended on fraud review with no worker lease.
3. Waiting on a durable settlement timer with no worker lease.
4. Done, with steps, checkpoints, result, split job and job-state rows, and events visible.

Use `--brief` to skip row output. Use `--all-columns` to add the complete `jobs_view` record at the
fraud-review boundary.

### Rows to inspect

The default Notice queries select only the fields that prove each claim. With `--all-columns`, the lab
first runs this explicit Explore query:

```sql
SELECT *
FROM jobs_view
WHERE job_id = @jobId;
```

The program primarily queries `jobs_view`, `steps_view`, `checkpoints_view`, and `events_view`. It goes
one level deeper only to compare `jobs` with `jobs_state` and to show the result stored on the `jobs_state` row. Those
base tables explain Acta's design; applications should normally use `IJobs`, and operational tooling
should prefer the curated views. Base tables are not the storage compatibility contract.

Notice that `execution_number` reaches three while every step keeps the one outcome it recorded. The
plain approval log appears twice because signal and timer recovery both re-enter the handler; the
completed step bodies are replay-skipped after their outcomes have been recorded.

### Break it

Reject the fraud review:

```bash
dotnet run --project learn/200-durable-execution/201-durable-checkout -- --reject
```

The same signal mechanism now ends the checkout as `cancelled`. The review runs before inventory and
payment, so the resulting rows prove that nothing was reserved or charged: no step, timer, or terminal
result was created.

This lab does not stage a process kill. Use [`220-at-most-once-step`](/learn/220-at-most-once-step.md) for the
ambiguous side-effect crash window and [`705-worker-crash-recovery`](/learn/705-worker-crash-recovery.md)
for lease expiry and takeover by another worker.

### When not to use

Use a database transaction when all work is local and atomic. A short idempotent background action does
not need this many durable facts. Prefer an external workflow engine when non-developers must author large,
versioned processes or cross-system orchestration is itself the product.

### Source trail

- [The Engineering Labs](https://github.com/acta-dotnet/acta/blob/main/docs/engineering-labs.md)
- [`Checkout.cs`](https://github.com/acta-dotnet/acta/blob/main/learn/200-durable-execution/201-durable-checkout/Checkout.cs)
- [`JobContext.cs`](https://github.com/acta-dotnet/acta/blob/main/src/Acta/Handlers/JobContext.cs)
- [`JobEntity.cs`](https://github.com/acta-dotnet/acta/blob/main/src/Acta/Domain/Entities/JobEntity.cs)
- [`JobStateEntity.cs`](https://github.com/acta-dotnet/acta/blob/main/src/Acta/Domain/Entities/JobStateEntity.cs)
- [`CheckpointEntity.cs`](https://github.com/acta-dotnet/acta/blob/main/src/Acta/Domain/Entities/CheckpointEntity.cs)
- [`StepEntity.cs`](https://github.com/acta-dotnet/acta/blob/main/src/Acta/Domain/Entities/StepEntity.cs)
