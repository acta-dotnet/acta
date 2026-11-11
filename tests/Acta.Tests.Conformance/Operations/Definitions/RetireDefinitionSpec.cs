using System.Collections.Immutable;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Definitions;

/// <summary>
/// The operator retire verb end to end: the definition flips to Retired, its parked jobs are
/// cancelled with an audited reason, a running attempt keeps its lease and finishes but its job never
/// runs again, enqueue is refused, and a build that still carries the definition brings it back.
/// </summary>
[ConformanceSpec(
    "definitions.retire",
    "Retire flips a definition and cancels its parked jobs",
    Area = "Catalog",
    Contract = "Retiring a definition cancels its parked jobs with reason JobDefinitionRetired, refuses enqueue, and lets a running attempt finish but never re-arms its job."
)]
public sealed class RetireDefinitionSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobsManifest>
{
    private const string Actor = "spec-operator";

    private const string Reason = "the handler left the build";

    [Fact(DisplayName = "Every parked job of the retired definition is cancelled with retention, no lease, and one audited event")]
    public async Task Parked_jobs_are_cancelled_with_an_audited_reason()
    {
        var ct = TestContext.Current.CancellationToken;

        var ready = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);
        var paused = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);
        Assert.Equal(ControlAction.Applied, (await Jobs.PauseAsync(paused, "parked for the retire", ct: ct)).Action);

        var suspended = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(suspended, ct));
        Assert.Equal(JobStatusCode.Suspended, (await ReadJobAsync(suspended.JobId, ct)).Status);

        var untouched = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))),
            ct
        );

        Assert.Equal(ControlAction.Applied, (await RetireAsync("job-wait-signal", ct)).Action);

        foreach (var cancelled in new[] { ready, paused, suspended })
        {
            var row = await ReadJobAsync(cancelled.JobId, ct);
            Assert.Equal(JobStatusCode.Cancelled, row.Status);
            Assert.NotNull(row.RetentionUntilUtc);
            Assert.Null(row.LeasedByWorkerId);

            Assert.Equal(1, await CountEventsAsync(cancelled.JobId, EventCode.JobCancelled, ct));
            var evt = await ReadSingleEventAsync(cancelled.JobId, EventCode.JobCancelled, ct);
            Assert.Equal(EventReason.DefinitionRetired, EventDetail(evt, "reason"));
            Assert.Equal(Actor, evt.Actor);
            Assert.Null(EventDetail(evt, "actor"));
            Assert.Equal(Reason, evt.EventMessage);
            Assert.Equal(0, evt.WorkerId);
        }

        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(untouched.JobId, ct)).Status);
    }

    [Fact(
        DisplayName = "The retire writes one definition-scoped retired event, leaves the definition Retired, and answers its new version"
    )]
    public async Task The_definition_is_retired_and_audited()
    {
        var ct = TestContext.Current.CancellationToken;

        var before = await Operations.Definitions.GetAsync(TestNamespace, "job-wait-signal", ct);
        Assert.NotNull(before);
        var retired = await RetireAsync("job-wait-signal", ct);
        Assert.Equal(ControlAction.Applied, retired.Action);

        var after = await Operations.Definitions.GetAsync(TestNamespace, "job-wait-signal", ct);
        Assert.NotNull(after);
        Assert.Equal(DefinitionStatusCode.Retired, after!.Status);
        Assert.True(after.Version > before!.Version);
        Assert.Equal(after.Version, retired.Version);

        var evt = await Db.From<EventEntity>()
            .Where(e => e.DefinitionId == before.DefinitionId && e.EventCode == EventCode.JobDefinitionRetired)
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(evt);
        Assert.Equal(EntityCode.Definition, evt!.EntityCode);
        Assert.Equal(before.DefinitionId, evt.EntityId);
        Assert.Equal(Actor, evt.Actor);
        Assert.Null(EventDetail(evt, "actor"));
        Assert.Equal(Reason, evt.EventMessage);
    }

    [Fact(DisplayName = "A retire stamps its definition past the namespace's newest stamp, so a heartbeat sees the edit")]
    public async Task A_retire_stamps_past_the_namespace_newest()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        // A writer stamped ahead of this one's clock committed first, as one this retire waited behind can.
        var now = DateTime.UtcNow.AddHours(1);
        var ahead = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        var stamped = await Db.From<DefinitionEntity>()
            .Where(d => d.NamespaceId == ns && d.DefinitionName == "job-wait-signal")
            .UpdateOnlyAsync(() => new DefinitionEntity { ModifiedAtUtc = ahead }, ct);
        Assert.Equal(1, stamped);

        Assert.Equal(ControlAction.Applied, (await RetireAsync("add-numbers", ct)).Action);

        var retired = await Db.From<DefinitionEntity>()
            .Where(d => d.NamespaceId == ns && d.DefinitionName == "add-numbers")
            .SingleOrDefaultAsync(ct);
        Assert.True(retired!.ModifiedAtUtc > ahead);
    }

    [Fact(DisplayName = "A running attempt keeps its lease through the retire and still completes")]
    public async Task A_running_attempt_is_left_to_finish()
    {
        var ct = TestContext.Current.CancellationToken;
        var (leaseTtl, ns, workerId) = await DepsAsync(ct);

        var running = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        var claimed = Assert.Single(
            await Services.GetRequiredService<ExecutionOperations>().ClaimJobAsync(ns, workerId, leaseTtl, running.JobId, ct)
        );

        Assert.Equal(ControlAction.Applied, (await RetireAsync("add-numbers", ct)).Action);

        var mid = await ReadJobAsync(running.JobId, ct);
        Assert.Equal(JobStatusCode.Executing, mid.Status);
        Assert.Equal(workerId, mid.LeasedByWorkerId);
        Assert.Equal(0, await CountEventsAsync(running.JobId, EventCode.JobCancelled, ct));

        var completion = await Services
            .GetRequiredService<ExecutionOperations>()
            .CompleteAsync(workerId, CompletionTestOps.Landing(claimed), ct);

        Assert.Equal(CompleteExecutionsAction.Completed, completion.Action);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(running.JobId, ct)).Status);
    }

    [Fact(DisplayName = "A failing attempt in flight at the retire lands Cancelled instead of retrying, and nothing claims it again")]
    public async Task A_failing_attempt_in_flight_at_the_retire_lands_cancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        // Held past any slow step: a timeout before the heartbeat learns of the retire would plan a retry.
        var running = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "chaos-holding", JobPayload.None), ct);
        ChaosProbes.Reset(running.JobId);

        var run = Runtime.RunOnceAsync(running, ct);
        await ChaosProbes.WaitStartedAsync(running.JobId, ct);
        Assert.Equal(ControlAction.Applied, (await RetireAsync("chaos-holding", ct)).Action);
        await RunAnsweredHeartbeatAsync(ct);
        ChaosProbes.Fail(running.JobId);

        Assert.Equal(RunOnceStatus.Completed, await run);
        await AssertCancelledByTheWorkerAsync(running.JobId, ct);
        Assert.Equal(RunOnceStatus.NothingClaimed, await Runtime.RunOnceAsync(running, ct));
    }

    [Fact(DisplayName = "A job that recovery reclaims after the retire lands Cancelled without its handler running")]
    public async Task A_reclaimed_job_of_a_retired_definition_never_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, ns, workerId) = await DepsAsync(ct);
        var stranded = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "chaos-counting", JobPayload.None), ct);
        ChaosProbes.Reset(stranded.JobId);
        await ChaosSpecHelpers.StrandExecutingAsync(Services, ns, workerId, stranded.JobId, ct);

        Assert.Equal(ControlAction.Applied, (await RetireAsync("chaos-counting", ct)).Action);
        Assert.Equal(1, await ChaosSpecHelpers.ReclaimAsync(Services, ns, ct));
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(stranded.JobId, ct)).Status);
        await RunAnsweredHeartbeatAsync(ct);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(stranded, ct));
        Assert.False(ChaosProbes.CountingInvocations.ContainsKey(stranded.JobId));
        await AssertCancelledByTheWorkerAsync(stranded.JobId, ct);
    }

    [Fact(DisplayName = "A worker that starts after the retire lands the reclaimed job Cancelled on its first claim, before any heartbeat")]
    public async Task A_worker_started_after_the_retire_never_runs_the_job_on_its_first_claim()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, ns, workerId) = await DepsAsync(ct);
        var stranded = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "chaos-counting", JobPayload.None), ct);
        ChaosProbes.Reset(stranded.JobId);
        await ChaosSpecHelpers.StrandExecutingAsync(Services, ns, workerId, stranded.JobId, ct);
        Assert.Equal(ControlAction.Applied, (await RetireAsync("chaos-counting", ct)).Action);
        Assert.Equal(1, await ChaosSpecHelpers.ReclaimAsync(Services, ns, ct));

        // Started, not deployed: a deploy by a build that declares the definition would make it Active again.
        await using var later = BuildProvider<TestJobs.TestJobsManifest>("after-retire");
        var runtime = later.GetServices<WorkerRuntime>().Single();
        await runtime.InitializeAsync(ct);

        Assert.Equal(RunOnceStatus.Completed, await runtime.RunOnceAsync(TestNamespace, stranded.JobId, ct));
        Assert.False(ChaosProbes.CountingInvocations.ContainsKey(stranded.JobId));
        await AssertCancelledByTheWorkerAsync(stranded.JobId, ct);
    }

    [Fact(
        DisplayName = "A worker that bounced a definition before its retire still claims the job recovery reclaims, and lands it Cancelled"
    )]
    public async Task A_reclaimed_job_of_a_retired_definition_this_worker_bounced_lands_cancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, ns, workerId) = await DepsAsync(ct);
        var stranded = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "chaos-counting", JobPayload.None), ct);
        await ChaosSpecHelpers.StrandExecutingAsync(Services, ns, workerId, stranded.JobId, ct);

        // A build without the handler bounces the definition's other job and stops claiming the definition.
        Assert.True(Runtime.TryGetDefinitionId("chaos-counting", out var definitionId));
        Assert.True(Runtime.Descriptors.TryRemove(definitionId, out _));
        await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "chaos-counting", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await TickUntilClaimedAsync(ct));

        Assert.Equal(ControlAction.Applied, (await RetireAsync("chaos-counting", ct)).Action);
        Assert.Equal(1, await ChaosSpecHelpers.ReclaimAsync(Services, ns, ct));
        await RunAnsweredHeartbeatAsync(ct);

        // The namespace claim, because a claim by id never consults the exclusion.
        Assert.Equal(RunOnceStatus.Completed, await TickUntilClaimedAsync(ct));
        await AssertCancelledByTheWorkerAsync(stranded.JobId, ct);
    }

    [Fact(DisplayName = "A Suspended parent waiting on a child of the retired definition is released without a recovery pass")]
    public async Task A_parent_waiting_on_a_cancelled_child_is_released()
    {
        var ct = TestContext.Current.CancellationToken;

        var parent = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-parent-one", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        Assert.Equal(JobStatusCode.Suspended, (await ReadJobAsync(parent.JobId, ct)).Status);

        var child = Assert.Single(await Db.From<JobEntity>().Where(j => j.ParentJobId == parent.JobId).ToListAsync(ct));

        Assert.Equal(ControlAction.Applied, (await RetireAsync("job-child-echo", ct)).Action);

        Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(child.Id, ct)).Status);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(parent.JobId, ct)).Status);

        var latch = Assert.Single(await ReadSignalsAsync(parent.JobId, ct));
        Assert.Equal($"sys.child.{child.Id}", latch.CheckpointName);
        Assert.Equal(CheckpointStatusCode.Set, latch.Status);
    }

    [Fact(DisplayName = "Enqueue against a retired definition is rejected with DefinitionRetired")]
    public async Task Enqueue_after_the_retire_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(ControlAction.Applied, (await RetireAsync("add-numbers", ct)).Action);

        var rejected = await Assert.ThrowsAsync<EnqueueRejectedException>(async () =>
            await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))), ct)
        );
        Assert.Equal(EnqueueRejectionReason.DefinitionRetired, rejected.Reason);
    }

    [Fact(DisplayName = "An unknown name is NotFound and a stale version is VersionConflict with the current version, and neither writes")]
    public async Task Unknown_is_not_found_and_a_stale_version_is_a_version_conflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await Operations.Definitions.GetAsync(TestNamespace, "add-numbers", ct);
        var eventsBefore = await CountDefinitionEventsAsync(before!.DefinitionId, ct);
        var parked = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))),
            ct
        );

        var missing = await Operations.Definitions.RetireAsync(TestNamespace, "no-such-job", 0, Reason, Actor, ct);
        Assert.Equal(ControlAction.NotFound, missing.Action);
        Assert.Null(missing.Version);

        var stale = await Operations.Definitions.RetireAsync(TestNamespace, "add-numbers", 9999, Reason, Actor, ct);
        Assert.Equal(ControlAction.VersionConflict, stale.Action);
        Assert.Equal(before.Version, stale.Version);

        // Neither call wrote: the row keeps its status and version, its parked job is still Ready, and
        // no definition-level event was recorded.
        var after = await Operations.Definitions.GetAsync(TestNamespace, "add-numbers", ct);
        Assert.Equal(DefinitionStatusCode.Active, after!.Status);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(JobStatusCode.Ready, (await Jobs.GetStatusAsync(parked, ct))?.Status);
        Assert.Equal(eventsBefore, await CountDefinitionEventsAsync(before.DefinitionId, ct));
    }

    [Fact(DisplayName = "Retiring an already retired definition is applied and writes no further events")]
    public async Task A_second_retire_is_applied_and_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;

        var parked = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);
        Assert.Equal(ControlAction.Applied, (await RetireAsync("job-wait-signal", ct)).Action);

        var definition = await Operations.Definitions.GetAsync(TestNamespace, "job-wait-signal", ct);
        var eventsBefore = await CountDefinitionEventsAsync(definition!.DefinitionId, ct);

        var second = await Operations.Definitions.RetireAsync(TestNamespace, "job-wait-signal", definition.Version, Reason, Actor, ct);
        Assert.Equal(ControlAction.Applied, second.Action);
        Assert.Equal(definition.Version, second.Version);

        Assert.Equal(eventsBefore, await CountDefinitionEventsAsync(definition.DefinitionId, ct));
        Assert.Equal(1, await CountEventsAsync(parked.JobId, EventCode.JobCancelled, ct));
        Assert.Equal(definition.Version, (await Operations.Definitions.GetAsync(TestNamespace, "job-wait-signal", ct))!.Version);
    }

    [Fact(DisplayName = "A registration that declares a retired definition makes it Active again")]
    public async Task Re_registration_reactivates_a_retired_definition()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var descriptors = ImmutableArray.Create(TestJobsManifest.Descriptors.Descriptors.Single(d => d.JobName == "add-numbers"));

        Assert.Equal(ControlAction.Applied, (await RetireAsync("add-numbers", ct)).Action);

        await DefinitionTestOps.RegisterAsync(Services, ns, descriptors, ct);
        Assert.Equal(DefinitionStatusCode.Active, (await Operations.Definitions.GetAsync(TestNamespace, "add-numbers", ct))!.Status);

        var accepted = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))),
            ct
        );
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(accepted.JobId, ct)).Status);
    }

    private async Task<RetireDefinitionResponse> RetireAsync(string jobName, CancellationToken ct)
    {
        var definition = await Operations.Definitions.GetAsync(TestNamespace, jobName, ct);
        Assert.NotNull(definition);
        return await Operations.Definitions.RetireAsync(TestNamespace, jobName, definition!.Version, Reason, Actor, ct);
    }

    /// <summary>Ticks the namespace claim until one claims a row, and answers that tick's outcome.</summary>
    private Task<RunOnceStatus> TickUntilClaimedAsync(CancellationToken ct) =>
        SpecWaits.RepeatUntilAsync(() => Runtime.RunOnceAsync(TestNamespace, ct), outcome => outcome != RunOnceStatus.NothingClaimed, ct);

    // The worker's landing for a retired definition: terminal with retention, and one cancel event naming the retirement.
    private async Task AssertCancelledByTheWorkerAsync(long jobId, CancellationToken ct)
    {
        var row = await ReadJobAsync(jobId, ct);
        Assert.Equal(JobStatusCode.Cancelled, row.Status);
        Assert.NotNull(row.RetentionUntilUtc);

        var evt = await ReadSingleEventAsync(jobId, EventCode.JobCancelled, ct);
        Assert.Equal(EventReason.DefinitionRetired, EventDetail(evt, "reason"));
        Assert.NotEqual(0, evt.WorkerId);
        Assert.Null(evt.Actor);
    }

    private async Task<int> CountDefinitionEventsAsync(int definitionId, CancellationToken ct) =>
        await Db.From<EventEntity>().Where(e => e.EntityCode == EntityCode.Definition && e.EntityId == definitionId).CountAsync(ct);

    private async Task<(int LeaseTtl, int Ns, int WorkerId)> DepsAsync(CancellationToken ct)
    {
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var leaseTtl = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;
        var worker = await Db.From<WorkerEntity>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);
        return (leaseTtl, ns, worker!.Id);
    }
}
