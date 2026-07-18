using Acta.Labs;
using Microsoft.Extensions.Hosting;

namespace Acta.Concepts.DurableCheckout;

internal sealed record CheckoutLabScenario(string Namespace, string OrderId, bool RejectFraudReview);

internal sealed class Primer(IJobs jobs, ConceptLab lab, CheckoutLabScenario scenario, IHostApplicationLifetime lifetime)
    : BackgroundService
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
            SELECT job_ref, namespace, status, execution_number, next_run_at_utc, leased_by_worker_id, lease_expires_at_utc
            FROM jobs_view
            WHERE job_id = @jobId
            """,
            new { jobId = checkout.JobId },
            ct
        );
        await ShowSplitStateAsync("The append-mostly job row and hot runtime row start together", checkout.JobId, ct);

        var release = await jobs.RescheduleAsync(checkout, DateTime.UnixEpoch, "release the HERO 201 enqueue snapshot", "concept-201", ct);
        if (release.Action != JobControlAction.Applied)
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
            SELECT job_ref, status, execution_number, leased_by_worker_id, lease_expires_at_utc
            FROM jobs_view
            WHERE job_id = @jobId
            """,
            new { jobId = checkout.JobId },
            ct
        );
        await lab.ShowAsync(
            "Completed external operations are durable step outcomes",
            """
            SELECT step_name, state, attempt_number, result_format
            FROM steps_view
            WHERE job_id = @jobId
            ORDER BY step_id
            """,
            new { jobId = checkout.JobId },
            ct
        );
        await lab.ShowAsync(
            "The charge variable and pending approval are checkpoint rows",
            """
            SELECT checkpoint_name, kind, state, value_format, due_at_utc, modified_at_utc
            FROM checkpoints_view
            WHERE job_id = @jobId
            ORDER BY checkpoint_name
            """,
            new { jobId = checkout.JobId },
            ct
        );

        var decision = new FraudDecision(!scenario.RejectFraudReview, "alice");
        await jobs.RaiseSignalAsync(checkout, "fraud-review", decision, ct: ct);
        Console.WriteLine($"[{scenario.OrderId}] fraud-review raised: {(decision.Approved ? "approved" : "rejected")}");

        if (scenario.RejectFraudReview)
        {
            await WaitForAsync(checkout.JobId, snapshot => snapshot.Status == JobStatusCode.Cancelled, "fraud rejection cancellation", ct);
            await lab.ShowAsync(
                "3. Rejected - cancellation is terminal and no timer or receipt was created",
                """
                SELECT job_ref, status, execution_number, failure_count, last_result_format
                FROM jobs_view
                WHERE job_id = @jobId
                """,
                new { jobId = checkout.JobId },
                ct
            );
            await lab.ShowAsync(
                "Only the inventory and payment steps exist",
                """
                SELECT step_name, state, attempt_number, result_format
                FROM steps_view
                WHERE job_id = @jobId
                ORDER BY step_id
                """,
                new { jobId = checkout.JobId },
                ct
            );
            await lab.ShowAsync(
                "The rejected signal is latched, but no timer checkpoint exists",
                """
                SELECT checkpoint_name, kind, state, value_format, due_at_utc
                FROM checkpoints_view
                WHERE job_id = @jobId
                ORDER BY checkpoint_name
                """,
                new { jobId = checkout.JobId },
                ct
            );
            await lab.ShowAsync(
                "Cancellation produces no terminal result row",
                """
                SELECT job_id, execution_number, result_format_id, created_at_utc
                FROM {{schema}}.results
                WHERE job_id = @jobId
                ORDER BY execution_number
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
            SELECT job_ref, status, execution_number, next_run_at_utc, leased_by_worker_id, lease_expires_at_utc
            FROM jobs_view
            WHERE job_id = @jobId
            """,
            new { jobId = checkout.JobId },
            ct
        );
        await lab.ShowAsync(
            "The signal is latched and the settlement timer is pending",
            """
            SELECT checkpoint_name, kind, state, due_at_utc, modified_at_utc
            FROM checkpoints_view
            WHERE job_id = @jobId
            ORDER BY checkpoint_name
            """,
            new { jobId = checkout.JobId },
            ct
        );

        await WaitForAsync(checkout.JobId, snapshot => snapshot.Status == JobStatusCode.Done, "terminal completion", ct);
        await lab.ShowAsync(
            "4. Done - one identity completed through three handler entries",
            """
            SELECT job_ref, status, execution_number, failure_count, last_result_format, leased_by_worker_id
            FROM jobs_view
            WHERE job_id = @jobId
            """,
            new { jobId = checkout.JobId },
            ct
        );
        await lab.ShowAsync(
            "All external operations have one successful durable outcome",
            """
            SELECT step_name, state, attempt_number, result_format
            FROM steps_view
            WHERE job_id = @jobId
            ORDER BY step_id
            """,
            new { jobId = checkout.JobId },
            ct
        );
        await lab.ShowAsync(
            "The variable survived, the signal stayed latched, and the timer was consumed",
            """
            SELECT checkpoint_name, kind, state, value_format, due_at_utc, modified_at_utc
            FROM checkpoints_view
            WHERE job_id = @jobId
            ORDER BY checkpoint_name
            """,
            new { jobId = checkout.JobId },
            ct
        );
        await lab.ShowAsync(
            "The terminal result has its own execution-stamped durable row",
            """
            SELECT job_id, execution_number, result_format_id, created_at_utc
            FROM {{schema}}.results
            WHERE job_id = @jobId
            ORDER BY execution_number
            """,
            new { jobId = checkout.JobId },
            ct
        );
        await ShowSplitStateAsync("The stable job row stayed put while the runtime row advanced", checkout.JobId, ct);
        await ShowLedgerAsync(checkout.JobId, ct);

        lifetime.StopApplication();
    }

    private async Task<JobSnapshot> WaitForAsync(long jobId, Func<JobSnapshot, bool> predicate, string description, CancellationToken ct)
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
            SELECT j.id AS job_id, j.input_format_id AS stable_input_format, j.created_at_utc AS stable_created_at_utc, r.status_code AS runtime_status_code, r.execution_number AS runtime_executions, r.failure_count AS runtime_failures, r.leased_by_worker_id AS runtime_worker, r.modified_at_utc AS runtime_modified_at_utc
            FROM {{schema}}.jobs AS j
            INNER JOIN {{schema}}.runtimes AS r ON r.job_id = j.id
            WHERE j.id = @jobId
            """,
            new { jobId },
            ct
        );

    private Task ShowLedgerAsync(long jobId, CancellationToken ct) =>
        lab.ShowAsync(
            "The append-only event ledger explains every transition",
            """
            SELECT event, from_status, to_status, execution_number, reason
            FROM events_view
            WHERE job_id = @jobId
            ORDER BY event_id
            """,
            new { jobId },
            ct
        );
}
