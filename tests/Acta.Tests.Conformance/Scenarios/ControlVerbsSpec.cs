using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for the <see cref="IJobs"/> control verbs (Cancel / Pause / Resume / Restart) against
/// a provider. Exercises the legal transitions, the rejected and not-found paths, the reason-message
/// persistence rule (row only for the reason-bearing states Paused/Cancelled; the event always), and
/// the audit stamping (<c>actor = Operator</c>, causal <c>reason = ControlManual</c>, past-tense event
/// code, captured from/to status). Verbs operate on a specific id, so no worker loop runs - the spec
/// never contends for claims with sibling tests on the shared schema.
/// </summary>
[ConformanceSpec(
    "control-verbs.transitions",
    "Cancel Pause Resume Restart apply legal transitions and audit",
    Area = "Control",
    Contract = "IJobs control verbs apply legal transitions stamping Operator/ControlManual, persist reason on reason-bearing states, reject illegal moves and report not-found.",
    Arrange = "A Ready job is enqueued with no worker loop contending for claims.",
    Act = "The job is paused, resumed, cancelled, and restarted, then an illegal resume-of-Ready and a control on a missing id are invoked.",
    Assert = "Legal transitions apply with Operator/ControlManual audit and persisted reasons while illegal moves reject and the missing id reports not-found."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.PauseJobAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.ResumeJobAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.CancelJobAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.RestartJobAsync))]
public abstract class ControlVerbsSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    protected override bool RunAsWorker => true;

    [Fact(DisplayName = "Pause then resume apply legal transitions and stamp the audit event with reason and from/to")]
    public async Task Pause_then_resume_round_trips_status_reason_and_events()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await EnqueueOneAsync(ct);

        var pause = await Jobs.PauseAsync(enqueued, "maintenance window", ct: ct);
        Assert.Equal(enqueued.JobId, pause.JobId);
        Assert.Equal(JobControlAction.Applied, pause.Action);
        Assert.Equal(JobStatusCode.Paused, pause.Status);

        var paused = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Paused, paused.Status);

        var pausedEvent = await ReadSingleEventAsync(enqueued.JobId, JobEventCode.JobPaused, ct);
        Assert.Equal(JobActorCode.Operator, pausedEvent.ActorCode);
        Assert.Equal(JobStatusCode.Ready, pausedEvent.FromStatus);
        Assert.Equal(JobStatusCode.Paused, pausedEvent.ToStatus);
        Assert.Equal(JobEventReasonCode.JobControlManual, pausedEvent.ReasonCode);
        Assert.Equal("maintenance window", pausedEvent.ReasonMessage);

        var resume = await Jobs.ResumeAsync(enqueued, "all clear", ct: ct);
        Assert.Equal(JobControlAction.Applied, resume.Action);
        Assert.Equal(JobStatusCode.Ready, resume.Status);

        var resumed = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, resumed.Status);

        // The message survives on the audit event.
        var resumedEvent = await ReadSingleEventAsync(enqueued.JobId, JobEventCode.JobResumed, ct);
        Assert.Equal(JobActorCode.Operator, resumedEvent.ActorCode);
        Assert.Equal(JobStatusCode.Paused, resumedEvent.FromStatus);
        Assert.Equal(JobStatusCode.Ready, resumedEvent.ToStatus);
        Assert.Equal("all clear", resumedEvent.ReasonMessage);
    }

    [Fact(DisplayName = "Cancel terminates the job and persists the reason on the row and the event")]
    public async Task Cancel_terminates_and_persists_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await EnqueueOneAsync(ct);

        var cancel = await Jobs.CancelAsync(enqueued, "no longer needed", actorKey: "spec-actor", ct: ct);
        Assert.Equal(JobControlAction.Applied, cancel.Action);
        Assert.Equal(JobStatusCode.Cancelled, cancel.Status);

        var cancelled = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Cancelled, cancelled.Status);
        Assert.Null(cancelled.LeasedByWorkerId);
        Assert.Null(cancelled.LeaseExpiresAtUtc);

        var cancelEvent = await ReadSingleEventAsync(enqueued.JobId, JobEventCode.JobCancelled, ct);
        Assert.Equal(JobActorCode.Operator, cancelEvent.ActorCode);
        Assert.Equal(JobStatusCode.Ready, cancelEvent.FromStatus);
        Assert.Equal(JobStatusCode.Cancelled, cancelEvent.ToStatus);
        // Reason is always recorded on the event.
        Assert.Equal(JobEventReasonCode.JobControlManual, cancelEvent.ReasonCode);
        Assert.Equal("no longer needed", cancelEvent.ReasonMessage);
        // The caller-supplied actor id (e.g. an authenticated principal name) persists on the event.
        Assert.Equal("spec-actor", cancelEvent.ActorKey);
    }

    [Fact(DisplayName = "Restart resets the failure budget and clears retention while keeping execution_number")]
    public async Task Restart_revives_terminal_resets_failure_clears_retention_keeps_execution_number()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await EnqueueOneAsync(ct);

        // Drive one real execution (own per-test namespace, so no cross-test claim contention) so
        // execution_number is non-zero - otherwise the "unchanged" assertion below would be a vacuous
        // 0 == 0 that couldn't catch a regression that zeroed it. The job ends terminal (Succeeded).
        var run = await Runtime.RunOnceAsync(enqueued, ct);
        Assert.Equal(RunOnceOutcome.Completed, run);

        var beforeRestart = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Succeeded, beforeRestart.Status);
        Assert.True(beforeRestart.ExecutionNumber > 0);

        var restart = await Jobs.RestartAsync(enqueued, "retry from scratch", ct: ct);
        Assert.Equal(JobControlAction.Applied, restart.Action);
        Assert.Equal(JobStatusCode.Ready, restart.Status);

        var restarted = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, restarted.Status);
        Assert.Equal(0, restarted.FailureCount);
        Assert.Null(restarted.RetentionUntilUtc);
        Assert.NotNull(restarted.NextRunAtUtc);
        // A restart is not a new attempt - the counter is left at its (non-zero) value, not reset.
        Assert.Equal(beforeRestart.ExecutionNumber, restarted.ExecutionNumber);

        // The message lives on the event.
        var restartEvent = await ReadSingleEventAsync(enqueued.JobId, JobEventCode.JobRestarted, ct);
        Assert.Equal(JobActorCode.Operator, restartEvent.ActorCode);
        Assert.Equal(JobStatusCode.Succeeded, restartEvent.FromStatus);
        Assert.Equal(JobStatusCode.Ready, restartEvent.ToStatus);
        Assert.Equal("retry from scratch", restartEvent.ReasonMessage);
    }

    [Fact(DisplayName = "Illegal control is Rejected with the current status")]
    public async Task Resume_non_paused_job_is_rejected_with_current_status()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await EnqueueOneAsync(ct);

        // Freshly enqueued job is Ready, not Paused.
        var resume = await Jobs.ResumeAsync(enqueued, ct: ct);
        Assert.Equal(JobControlAction.Rejected, resume.Action);
        Assert.Equal(JobStatusCode.Ready, resume.Status);

        var status = await Jobs.GetStatusAsync(enqueued, ct);
        Assert.Equal(JobStatusCode.Ready, status);
    }

    [Fact(DisplayName = "Control on a missing job is NotFound")]
    public async Task Control_on_missing_job_is_not_found()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await Jobs.CancelAsync(JobLookup.ById(long.MaxValue), ct: ct);
        Assert.Equal(JobControlAction.NotFound, result.Action);
        Assert.Null(result.Status);
    }

    private async Task<JobEnqueueOutcome> EnqueueOneAsync(CancellationToken ct)
    {
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(2, 3));

        return await Jobs.EnqueueAsync(
            new JobEnqueueRequest(
                JobNamespace: TestNamespace,
                JobName: "add-numbers",
                Input: payload,
                DeduplicationKey: TestKey("ctrl"),
                CorrelationKey: null,
                Priority: null
            ),
            ct
        );
    }
}
