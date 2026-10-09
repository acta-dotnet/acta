using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Execution;

/// <summary>Conformance for <c>arm_or_consume_sleep_timer</c>, the durable timer behind <c>ctx.SleepAsync</c>.</summary>
[ConformanceSpec(
    "arm-or-consume-sleep-timer.decision",
    "A sleep arms one durable timer, suspends until it is due, then consumes it",
    Area = "Execution",
    Contract = "A sleep arms one Pending timer and suspends on its stored instant until that passes, then consumes it once and continues on every later entry."
)]
public sealed class ArmOrConsumeSleepTimerSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private const string TimerName = "t.nap";

    private Acta.Execution Execution => Services.GetRequiredService<Acta.Execution>();

    [Fact(DisplayName = "A first sleep arms a Pending timer at its due instant and suspends")]
    public async Task First_sleep_arms_a_pending_timer()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var dbNow = await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);

        var armed = await SleepAsync(job.JobId, TimerName, delaySeconds: 600, ct);

        Assert.Equal(ArmOrConsumeSleepTimerOutcomeCode.Suspend, armed.Outcome);
        var due = Assert.NotNull(armed.DueAtUtc);
        Assert.InRange(due, dbNow.AddSeconds(599), dbNow.AddSeconds(660));

        var timer = Assert.Single(await TimersAsync(job.JobId, ct));
        Assert.Equal(TimerName, timer.Name);
        Assert.Equal(CheckpointStatusCode.Pending, timer.Status);
        Assert.Equal(due, timer.DueAtUtc);
    }

    [Fact(DisplayName = "Re-entering before the instant suspends on the stored instant, whatever delay the replay passes")]
    public async Task Re_entry_before_the_instant_keeps_the_stored_instant()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var armed = await SleepAsync(job.JobId, TimerName, delaySeconds: 600, ct);

        var replayedDelay = await SleepAsync(job.JobId, TimerName, delaySeconds: 1200, ct);
        var replayedInstant = await Execution.ArmOrConsumeSleepTimerAsync(
            new ArmOrConsumeSleepTimerCommand(job.JobId, TimerName, null, DateTime.UtcNow.AddDays(1)),
            ct
        );

        Assert.Equal(armed, replayedDelay);
        Assert.Equal(armed, replayedInstant);
        var timer = Assert.Single(await TimersAsync(job.JobId, ct));
        Assert.Equal(armed.DueAtUtc, timer.DueAtUtc);
        Assert.Equal(0, timer.Version);
    }

    [Fact(DisplayName = "Re-entering after the instant consumes the timer, continues, and clears the executing job's run instant")]
    public async Task Re_entry_after_the_instant_consumes_the_timer()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        await StartExecutingAsync(job, ct);
        await SleepAsync(job.JobId, TimerName, delaySeconds: 600, ct);
        await MakeDueAsync(job.JobId, ct);
        var before = await ReadJobAsync(job.JobId, ct);

        var consumed = await SleepAsync(job.JobId, TimerName, delaySeconds: 600, ct);

        Assert.Equal(new ArmOrConsumeSleepTimerResponse(ArmOrConsumeSleepTimerOutcomeCode.Continue, null), consumed);
        var timer = Assert.Single(await TimersAsync(job.JobId, ct));
        Assert.Equal(CheckpointStatusCode.Consumed, timer.Status);
        Assert.Equal(1, timer.Version);

        var after = await ReadJobAsync(job.JobId, ct);
        Assert.Equal(JobStatusCode.Executing, after.Status);
        Assert.Null(after.NextRunAtUtc);
        Assert.Equal(before.Version + 1, after.Version);
    }

    [Fact(DisplayName = "A consumed timer continues on every later entry and is never armed again")]
    public async Task Consumed_timer_continues_without_writing()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        await SleepAsync(job.JobId, TimerName, delaySeconds: 600, ct);
        await MakeDueAsync(job.JobId, ct);
        Assert.Equal(ArmOrConsumeSleepTimerOutcomeCode.Continue, (await SleepAsync(job.JobId, TimerName, delaySeconds: 600, ct)).Outcome);
        var consumed = Assert.Single(await TimersAsync(job.JobId, ct));

        Assert.Equal(
            new ArmOrConsumeSleepTimerResponse(ArmOrConsumeSleepTimerOutcomeCode.Continue, null),
            await SleepAsync(job.JobId, TimerName, delaySeconds: 600, ct)
        );

        var after = Assert.Single(await TimersAsync(job.JobId, ct));
        Assert.Equal(CheckpointStatusCode.Consumed, after.Status);
        Assert.Equal(consumed.Version, after.Version);
    }

    [Fact(DisplayName = "A zero-length sleep or one whose resume instant has passed continues at once and arms nothing")]
    public async Task Sleep_that_is_already_due_arms_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);

        var zero = await SleepAsync(job.JobId, "t.zero", delaySeconds: 0, ct);
        var past = await Execution.ArmOrConsumeSleepTimerAsync(
            new ArmOrConsumeSleepTimerCommand(job.JobId, "t.past", null, DateTime.UtcNow.AddMinutes(-1)),
            ct
        );

        Assert.Equal(new ArmOrConsumeSleepTimerResponse(ArmOrConsumeSleepTimerOutcomeCode.Continue, null), zero);
        Assert.Equal(new ArmOrConsumeSleepTimerResponse(ArmOrConsumeSleepTimerOutcomeCode.Continue, null), past);
        Assert.Empty(await TimersAsync(job.JobId, ct));
    }

    [Fact(DisplayName = "A second sleep under another name while one is pending is rejected and arms nothing")]
    public async Task Second_pending_sleep_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        Assert.Equal(ArmOrConsumeSleepTimerOutcomeCode.Suspend, (await SleepAsync(job.JobId, TimerName, delaySeconds: 600, ct)).Outcome);

        var rival = await SleepAsync(job.JobId, "t.rival", delaySeconds: 600, ct);

        Assert.Equal(new ArmOrConsumeSleepTimerResponse(ArmOrConsumeSleepTimerOutcomeCode.Reject, null), rival);
        Assert.Equal(TimerName, Assert.Single(await TimersAsync(job.JobId, ct)).Name);
    }

    private async Task<EnqueueJobResponse> EnqueueAsync(CancellationToken ct) =>
        await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))), ct);

    private Task<ArmOrConsumeSleepTimerResponse> SleepAsync(long jobId, string name, int delaySeconds, CancellationToken ct) =>
        Execution.ArmOrConsumeSleepTimerAsync(new ArmOrConsumeSleepTimerCommand(jobId, name, delaySeconds, null), ct);

    private async Task StartExecutingAsync(EnqueueJobResponse job, CancellationToken ct)
    {
        const int leaseTtlSeconds = 30;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var worker = await Db.From<WorkerEntity>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);
        var claimed = Assert.Single(await Execution.ClaimOneAsync(ns, worker!.Id, leaseTtlSeconds, job, ct));
        Assert.Equal(
            StartExecutionAction.Started,
            (await Execution.StartExecutionAsync(claimed.JobId, worker.Id, claimed.ExecutionNumber, leaseTtlSeconds, ct)).Action
        );
    }

    private async Task MakeDueAsync(long jobId, CancellationToken ct)
    {
        var past = DateTime.UtcNow.AddMinutes(-5);
        var affected = await Db.From<CheckpointEntity>()
            .Where(c => c.JobId == jobId && c.Kind == CheckpointKindCode.Timer && c.Name == TimerName)
            .UpdateOnlyAsync(() => new CheckpointEntity { DueAtUtc = past }, ct);
        Assert.Equal(1, affected);
    }

    private Task<IReadOnlyList<CheckpointEntity>> TimersAsync(long jobId, CancellationToken ct) =>
        Db.From<CheckpointEntity>().Where(c => c.JobId == jobId && c.Kind == CheckpointKindCode.Timer).ToListAsync(ct);
}
