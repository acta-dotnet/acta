using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Execution;

/// <summary>
/// Conformance for <c>events.wait_ms</c>: each execution's own event records how long it sat due before its claim, from
/// the instant that execution came due. A wait is bounded by database-clock reads taken around the run, never by a
/// constant: its claim falls between them.
/// </summary>
[ConformanceSpec(
    "execution.complete-executions-wait",
    "An execution's event records how long it waited to be claimed",
    Area = "Execution",
    Contract = "Each execution's own event records the claim instant minus the instant that execution came due, on the database clock, and every other event records no wait."
)]
[Deploys("add-numbers", "flaky-recover", "job-sleep-basic")]
public sealed class CompleteExecutionsWaitSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<CompleteExecutionsWaitSpec>>
{
    private IActaClock Clock => Services.GetRequiredService<IActaClock>();

    [Fact(
        DisplayName = "A job claimed ten minutes after it came due records that wait through the claim scan, and one claimed as it came due records a small one, both in the view and the event list"
    )]
    public async Task The_wait_runs_from_the_due_instant_to_the_claim()
    {
        var ct = TestContext.Current.CancellationToken;
        var late = await Jobs.EnqueueAsync(AddNumbers(), ct);
        var lateDue = await DueAgoAsync(late.JobId, TimeSpan.FromMinutes(10), ct);

        var lateRun = await RunAsync(
            () =>
                SpecWaits.RepeatUntilAsync(
                    () => Runtime.RunOnceAsync(TestNamespace, ct),
                    static s => s != RunOnceStatus.NothingClaimed,
                    ct
                ),
            RunOnceStatus.Completed,
            ct
        );

        var lateEvent = await OwnEventAsync(late.JobId, EventCode.JobExecutionSucceeded, 1, ct);
        AssertWaitBetween(lateEvent.WaitMs, lateDue, lateRun);
        Assert.True(lateEvent.WaitMs >= 600_000, $"waited {lateEvent.WaitMs} ms");
        Assert.Equal(lateEvent.WaitMs, await ViewWaitAsync(lateEvent.Id, ct));

        var prompt = await Jobs.EnqueueAsync(AddNumbers(), ct);
        var promptDue = await DueAsync(prompt.JobId, ct);

        var promptRun = await RunAsync(() => Runtime.RunOnceAsync(prompt, ct), RunOnceStatus.Completed, ct);

        var promptEvent = await OwnEventAsync(prompt.JobId, EventCode.JobExecutionSucceeded, 1, ct);
        AssertWaitBetween(promptEvent.WaitMs, promptDue, promptRun);
        Assert.Equal(promptEvent.WaitMs, await ViewWaitAsync(promptEvent.Id, ct));
    }

    [Fact(DisplayName = "A failed execution records its wait, and the retry records its own from the instant the retry came due")]
    public async Task A_failed_execution_and_its_retry_each_record_their_own_wait()
    {
        var ct = TestContext.Current.CancellationToken;
        FlakyRecoverProbe.Reset(TestNamespace);
        var flaky = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "flaky-recover", JobPayload.None), ct);
        var firstDue = await DueAgoAsync(flaky.JobId, TimeSpan.FromMinutes(10), ct);

        var firstRun = await RunAsync(() => Runtime.RunOnceAsync(flaky, ct), RunOnceStatus.Rearmed, ct);
        var retryDue = await DueAsync(flaky.JobId, ct);
        var secondRun = await RunAsync(() => Runtime.RunOnceAsync(flaky, ct), RunOnceStatus.Completed, ct);

        var failed = await OwnEventAsync(flaky.JobId, EventCode.JobExecutionFailed, 1, ct);
        AssertWaitBetween(failed.WaitMs, firstDue, firstRun);
        Assert.True(failed.WaitMs >= 600_000, $"waited {failed.WaitMs} ms");
        // Measured from the first due instant, the retry's wait would exceed ten minutes; its bound is the run's span.
        AssertWaitBetween((await OwnEventAsync(flaky.JobId, EventCode.JobExecutionSucceeded, 2, ct)).WaitMs, retryDue, secondRun);
    }

    [Fact(
        DisplayName = "An execution that yields on a sleep records its wait, the execution its wake resumes records its own from the wake instant, and the sleep's own event records none"
    )]
    public async Task A_sleep_and_its_resumption_each_record_their_own_wait()
    {
        var ct = TestContext.Current.CancellationToken;
        var sleeper = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-sleep-basic", JobPayload.None), ct);
        var firstDue = await DueAgoAsync(sleeper.JobId, TimeSpan.FromMinutes(10), ct);

        var firstRun = await RunAsync(() => Runtime.RunOnceAsync(sleeper, ct), RunOnceStatus.Rearmed, ct);

        // The timer comes due two minutes ago: the wake instant the resumed execution waits from.
        var wake = await DueAgoAsync(sleeper.JobId, TimeSpan.FromMinutes(2), ct);
        await Db.ExecuteRawAsync(
            "UPDATE {schema}.checkpoints SET due_at_utc = @p_due"
                + " WHERE job_id = @p_id AND checkpoint_kind_code = 30 /* CheckpointKindCode.Timer */ AND checkpoint_name = 'nap'",
            ct,
            ("@p_due", wake),
            ("@p_id", sleeper.JobId)
        );
        var resumedRun = await RunAsync(() => Runtime.RunOnceAsync(sleeper, ct), RunOnceStatus.Completed, ct);

        AssertWaitBetween((await OwnEventAsync(sleeper.JobId, EventCode.JobExecutionYielded, 1, ct)).WaitMs, firstDue, firstRun);
        var resumed = await OwnEventAsync(sleeper.JobId, EventCode.JobExecutionSucceeded, 2, ct);
        AssertWaitBetween(resumed.WaitMs, wake, resumedRun);
        Assert.True(resumed.WaitMs >= 120_000, $"waited {resumed.WaitMs} ms");
        Assert.Null(Assert.Single(await EventsAsync(sleeper.JobId, EventCode.JobSlept, ct)).WaitMs);
    }

    [Fact(DisplayName = "An operator's pause and resume of a job that never ran record no wait")]
    public async Task Events_with_no_execution_behind_them_record_no_wait()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await Jobs.EnqueueAsync(AddNumbers(), ct);

        Assert.Equal(ControlAction.Applied, (await Jobs.PauseAsync(JobLookup.ById(job.JobId), ct: ct)).Action);
        Assert.Equal(ControlAction.Applied, (await Jobs.ResumeAsync(JobLookup.ById(job.JobId), ct: ct)).Action);

        var paused = Assert.Single(await EventsAsync(job.JobId, EventCode.JobPaused, ct));
        var resumed = Assert.Single(await EventsAsync(job.JobId, EventCode.JobResumed, ct));
        Assert.Null(paused.WaitMs);
        Assert.Null(resumed.WaitMs);
        Assert.Null(await ViewWaitAsync(paused.Id, ct));
    }

    private EnqueueJobRequest AddNumbers() => new(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2)));

    /// <summary>Moves the job's next run <paramref name="ago"/> before the database clock and answers the instant as stored.</summary>
    private async Task<DateTime> DueAgoAsync(long jobId, TimeSpan ago, CancellationToken ct)
    {
        var due = DateTime.SpecifyKind(await Clock.GetUtcNowAsync(ct) - ago, DateTimeKind.Utc);
        await Db.ExecuteRawAsync(
            "UPDATE {schema}.jobs_state SET next_run_at_utc = @p_next WHERE job_id = @p_id",
            ct,
            ("@p_next", due),
            ("@p_id", jobId)
        );
        return await DueAsync(jobId, ct);
    }

    private async Task<DateTime> DueAsync(long jobId, CancellationToken ct) => Assert.NotNull((await ReadJobAsync(jobId, ct)).NextRunAtUtc);

    /// <summary>Runs one tick that must answer <paramref name="expected"/>, between two reads of the database clock.</summary>
    private async Task<(DateTime Before, DateTime After)> RunAsync(
        Func<Task<RunOnceStatus>> run,
        RunOnceStatus expected,
        CancellationToken ct
    )
    {
        var before = await Clock.GetUtcNowAsync(ct);
        Assert.Equal(expected, await run());
        return (before, await Clock.GetUtcNowAsync(ct));
    }

    /// <summary>
    /// A claim made between the run's two clock reads waited at least from <paramref name="due"/> to the first and at most
    /// to the second, in whole milliseconds, and never less than none.
    /// </summary>
    private static void AssertWaitBetween(int? waitMs, DateTime due, (DateTime Before, DateTime After) run)
    {
        Assert.NotNull(waitMs);
        Assert.InRange(
            waitMs.Value,
            Math.Max(0, (int)(run.Before - due).TotalMilliseconds),
            Math.Max(0, (int)(run.After - due).TotalMilliseconds)
        );
    }

    /// <summary>The job's events of <paramref name="code"/>, as the event list answers them.</summary>
    private async Task<IReadOnlyList<JobEventRecord>> EventsAsync(long jobId, EventCode code, CancellationToken ct) =>
        [.. (await GetEventsByJobId.Run(Services, jobId, ct)).Where(e => e.EventCode == code)];

    /// <summary>The event <paramref name="code"/> the execution numbered <paramref name="executionNumber"/> ended with.</summary>
    private async Task<JobEventRecord> OwnEventAsync(long jobId, EventCode code, long executionNumber, CancellationToken ct) =>
        Assert.Single(await EventsAsync(jobId, code, ct), e => e.ExecutionNumber == executionNumber);

    /// <summary>The event's <c>wait_ms</c> as the operator view <c>events_view</c> answers it.</summary>
    private async Task<int?> ViewWaitAsync(long eventId, CancellationToken ct)
    {
        await using var connection = await Db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT wait_ms FROM {Db.Qualify("events_view")} WHERE event_id = {eventId}";
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
