using System.Diagnostics;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for the typed <see cref="IJobs"/> façade: <c>EnqueueAsync&lt;TInput&gt;</c> (route
/// resolution + serialization), <see cref="JobEnqueueOptions"/> (deduplication key / delayed run),
/// <c>RunAndWaitAsync&lt;TInput, TResult&gt;</c> (enqueue + wait + typed result), and the delayed-enqueue
/// claim gate (<c>next_run_at_utc</c>). Uses the <c>add-numbers</c> job (input <see cref="AddNumbers"/>,
/// result <see cref="AddNumbersResult"/>) registered under the per-test namespace.
/// </summary>
[ConformanceSpec(
    "typed-enqueue.facade",
    "Typed enqueue resolves the route and delayed jobs gate on next_run",
    Area = "Enqueue",
    Contract = "The typed IJobs façade resolves the route from the input type, applies deduplication-key dedupe and delayed-run options, and RunAndWaitAsync waits.",
    Arrange = "The add-numbers job and companion probe definitions are registered with typed inputs and results under the per-test namespace.",
    Act = "Typed inputs including scalars are enqueued with deduplication-key and delayed options, and RunAndWaitAsync is driven to completion.",
    Assert = "Each route is resolved from the input type and the typed result round-trips back to the caller."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueOneAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueBatchAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.GetJobResultAsync))]
public abstract class TypedEnqueueSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    protected override bool RunAsWorker => true;

    [Fact(DisplayName = "Typed enqueue resolves the route, serializes the input and round-trips the result")]
    public async Task Typed_enqueue_resolves_route_serializes_and_round_trips_result()
    {
        var ct = TestContext.Current.CancellationToken;

        var outcome = await Jobs.EnqueueAsync(new AddNumbers(2, 3), ct: ct);
        Assert.Equal(JobEnqueueAction.Inserted, outcome.Action);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(outcome, ct));

        var result = await Jobs.GetResultAsync<AddNumbersResult>(outcome, ct);
        Assert.NotNull(result);
        Assert.Equal(5, result!.Sum);
    }

    [Fact(DisplayName = "Typed enqueue round-trips scalar value-type inputs without misclassifying them as none")]
    public async Task Typed_enqueue_round_trips_scalar_value_type_inputs()
    {
        var ct = TestContext.Current.CancellationToken;

        // Regression: scalar value types (int, double) must survive typed enqueue. They were once
        // misclassified as the 'none' payload format and arrived at the handler as default(T).
        var intJob = await Jobs.EnqueueAsync(21, ct: ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(intJob, ct));
        Assert.Equal(42, await Jobs.GetResultAsync<int>(intJob, ct));

        var doubleJob = await Jobs.EnqueueAsync(9.0, ct: ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(doubleJob, ct));
        Assert.Equal(4.5, await Jobs.GetResultAsync<double>(doubleJob, ct));
    }

    [Fact(DisplayName = "Typed enqueue applies the deduplication key and a repeat deduplicates onto one row")]
    public async Task Typed_enqueue_options_apply_deduplication_key_and_dedupe()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new JobEnqueueOptions
        {
            DeduplicationKey = "typed-invoice-1",
            Priority = JobPriorityCode.High,
            Tags = [new TagInput("billing", null), new TagInput("team", "payments")],
        };

        var first = await Jobs.EnqueueAsync(new AddNumbers(1, 1), options, ct);
        Assert.Equal(JobEnqueueAction.Inserted, first.Action);

        var snapshot = await Jobs.GetAsync(first, ct);
        Assert.Equal("typed-invoice-1", snapshot!.DeduplicationKey);

        // Same deduplication key dedupes onto the same row.
        var again = await Jobs.EnqueueAsync(new AddNumbers(1, 1), new JobEnqueueOptions { DeduplicationKey = "typed-invoice-1" }, ct);
        Assert.Equal(JobEnqueueAction.Deduplicated, again.Action);
        Assert.Equal(first.JobId, again.JobId);
    }

    [Fact(DisplayName = "A delayed job is not claimable before due but runs once due")]
    public async Task Delayed_enqueue_is_not_claimable_before_due_but_runs_when_due()
    {
        var ct = TestContext.Current.CancellationToken;

        // Future next_run_at_utc holds the job at Ready; the claim filter skips it.
        var future = await Jobs.EnqueueAsync(
            new AddNumbers(2, 2),
            new JobEnqueueOptions { NextRunAtUtc = DateTime.UtcNow.AddHours(1) },
            ct
        );
        Assert.Equal(RunOnceOutcome.NothingClaimed, await Runtime.RunOnceAsync(TestNamespace, future.JobId, ct));
        var stillReady = await Jobs.GetAsync(future, ct);
        Assert.Equal(JobStatusCode.Ready, stillReady!.Status);

        // A due (past) next_run_at_utc is immediately claimable.
        var due = await Jobs.EnqueueAsync(
            new AddNumbers(3, 4),
            new JobEnqueueOptions { NextRunAtUtc = DateTime.UtcNow.AddMinutes(-1) },
            ct
        );
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(due, ct));
        var result = await Jobs.GetResultAsync<AddNumbersResult>(due, ct);
        Assert.Equal(7, result!.Sum);
    }

    [Fact(DisplayName = "A handler returning a null result fails the job and stores no result")]
    public async Task Handler_returning_null_result_fails_the_job_and_stores_no_result()
    {
        var ct = TestContext.Current.CancellationToken;

        var outcome = await Jobs.EnqueueAsync(new NullResultInput(1), ct: ct);
        // Null contract: a null Task<T> return is a handler bug -> terminal Failed (MaxAttempts = 1),
        // never stored as a result.
        Assert.Equal(RunOnceOutcome.Failed, await Runtime.RunOnceAsync(outcome, ct));

        var snapshot = await Jobs.GetAsync(outcome, ct);
        Assert.Equal(JobStatusCode.Failed, snapshot!.Status);

        var result = await Jobs.GetResultAsync<NullResultOutput>(outcome, ct);
        Assert.Null(result);
    }

    [Fact(DisplayName = "RunAndWaitAsync enqueues, waits for completion and returns the typed result")]
    public async Task ExecuteAndWaitAsync_enqueues_waits_and_returns_typed_result()
    {
        var ct = TestContext.Current.CancellationToken;

        // RunAndWaitAsync polls for terminal status; drive worker ticks concurrently so the job runs.
        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var driver = Task.Run(async () =>
        {
            while (!loopCts.IsCancellationRequested)
            {
                try
                {
                    await Runtime.RunOnceAsync(TestNamespace, loopCts.Token);
                    await Task.Delay(50, loopCts.Token);
                }
                catch (Exception) when (loopCts.IsCancellationRequested)
                {
                    // Teardown cancels an in-flight tick; under a slow SQL Server SqlClient can surface
                    // that as a SqlException ("Operation cancelled by user") rather than
                    // OperationCanceledException: both are expected once we are shutting down.
                    return;
                }
            }
        });

        try
        {
            var outcome = await Jobs.RunAndWaitAsync<AddNumbers, AddNumbersResult>(
                new AddNumbers(4, 5),
                new JobExecutionOptions { WaitTimeout = TimeSpan.FromSeconds(60), PollInterval = TimeSpan.FromMilliseconds(100) },
                ct
            );

            Assert.True(outcome.IsSuccess);
            Assert.Equal(9, outcome.ValueOrThrow().Sum);
        }
        finally
        {
            await loopCts.CancelAsync();
            try
            {
                await driver;
            }
            catch (OperationCanceledException) { }
        }
    }

    [Fact(DisplayName = "RunAndWaitAsync throws when a Succeeded job stored no typed result")]
    public async Task ExecuteAndWaitAsync_typed_result_against_result_less_job_throws()
    {
        var ct = TestContext.Current.CancellationToken;

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var driver = Task.Run(async () =>
        {
            while (!loopCts.IsCancellationRequested)
            {
                try
                {
                    await Runtime.RunOnceAsync(TestNamespace, loopCts.Token);
                    await Task.Delay(50, loopCts.Token);
                }
                catch (Exception) when (loopCts.IsCancellationRequested)
                {
                    // Teardown cancels an in-flight tick; under a slow SQL Server SqlClient can surface
                    // that as a SqlException ("Operation cancelled by user") rather than
                    // OperationCanceledException: both are expected once we are shutting down.
                    return;
                }
            }
        });

        try
        {
            // policy-probe completes Succeeded but stores no result; requesting a typed result is a
            // caller contract mismatch, not a default(TResult).
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Jobs.RunAndWaitAsync<PolicyProbe, AddNumbersResult>(
                        new PolicyProbe("no-result"),
                        new JobExecutionOptions { WaitTimeout = TimeSpan.FromSeconds(60), PollInterval = TimeSpan.FromMilliseconds(100) },
                        ct
                    )
                    .AsTask()
            );
            Assert.Contains("stored no result", ex.Message);
        }
        finally
        {
            await loopCts.CancelAsync();
            try
            {
                await driver;
            }
            catch (OperationCanceledException) { }
        }
    }

    [Fact(DisplayName = "RunAndWaitAsync honors WaitTimeout when PollInterval exceeds it")]
    public async Task ExecuteAndWaitAsync_returns_at_wait_timeout_when_poll_interval_is_longer()
    {
        var ct = TestContext.Current.CancellationToken;

        // No worker drives ticks, so the job never terminates; the wait must end at WaitTimeout even
        // though a full PollInterval sleep would overshoot it many times over.
        var start = Stopwatch.GetTimestamp();
        var outcome = await Jobs.RunAndWaitAsync<AddNumbers, AddNumbersResult>(
            new AddNumbers(1, 2),
            new JobExecutionOptions { WaitTimeout = TimeSpan.FromMilliseconds(500), PollInterval = TimeSpan.FromSeconds(30) },
            ct
        );

        Assert.True(outcome.IsTimedOut);
        var elapsed = Stopwatch.GetElapsedTime(start);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"RunAndWaitAsync took {elapsed} against a 500ms WaitTimeout");
    }

    [Fact(DisplayName = "RunAndWaitAsync rejects non-positive wait options before enqueue")]
    public async Task ExecuteAndWaitAsync_rejects_non_positive_wait_options_before_enqueue()
    {
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Jobs.RunAndWaitAsync<AddNumbers, AddNumbersResult>(
                    new AddNumbers(1, 1),
                    new JobExecutionOptions { WaitTimeout = TimeSpan.Zero },
                    ct
                )
                .AsTask()
        );

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Jobs.RunAndWaitAsync<AddNumbers, AddNumbersResult>(
                    new AddNumbers(1, 1),
                    new JobExecutionOptions { PollInterval = TimeSpan.FromMilliseconds(-1) },
                    ct
                )
                .AsTask()
        );
    }
}
