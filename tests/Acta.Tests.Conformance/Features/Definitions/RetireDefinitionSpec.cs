using System.Collections.Immutable;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Definitions;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Definitions;

/// <summary>
/// The operator retire verb end to end: the definition flips to Retired, its parked jobs are
/// cancelled with an audited reason, a running attempt keeps its lease, enqueue is refused, and a
/// build that still carries the definition brings it back.
/// </summary>
[ConformanceSpec(
    "definitions.retire",
    "Retire flips a definition and cancels its parked jobs",
    Area = "Catalog",
    Contract = "Retiring a definition cancels its Ready, Suspended and Paused jobs with reason JobDefinitionRetired, leaves a running attempt alone, and refuses enqueue.",
    Arrange = "A definition carries one job in each parked status, one running attempt, and a Suspended parent waiting on a child of it.",
    Act = "An operator retires the definition by its natural key and version, then enqueues again and re-registers the manifest.",
    Assert = "Parked jobs are Cancelled with retention and one audited event each, the attempt still completes, enqueue is rejected, and an equal generation reactivates."
)]
[CoversStoreMethod(typeof(IDefinitionStore), nameof(IDefinitionStore.RetireDefinitionAsync))]
public abstract class RetireDefinitionSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const string ActorKey = "spec-operator";

    private const string Reason = "the handler left the build";

    private static readonly DateTime Generation = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime OlderGeneration = new(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        // Pinned so the re-registration facts can name a generation on either side of the stored one.
        services.Configure<JobsOptions>(o => o.ManifestGenerationUtc = Generation);
    }

    [Fact(DisplayName = "Every parked job of the retired definition is cancelled with retention, no lease, and one audited event")]
    public async Task Parked_jobs_are_cancelled_with_an_audited_reason()
    {
        var ct = TestContext.Current.CancellationToken;

        var ready = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);
        var paused = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);
        Assert.Equal(ControlAction.Applied, (await Jobs.PauseAsync(paused, "parked for the retire", ct: ct)).Action);

        var suspended = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(suspended, ct));
        Assert.Equal(JobStatusCode.Suspended, (await ReadJobAsync(suspended.JobId, ct)).Status);

        var untouched = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))),
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
            Assert.Equal(JobEventReasonCode.JobDefinitionRetired, evt.ReasonCode);
            Assert.Equal(ActorCode.Operator, evt.ActorCode);
            Assert.Equal(ActorKey, evt.ActorKey);
            Assert.Equal(Reason, evt.ReasonMessage);
            Assert.Equal(JobStatusCode.Cancelled, evt.ToStatus);
        }

        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(untouched.JobId, ct)).Status);
    }

    [Fact(DisplayName = "The retire writes one definition-scoped retired event and leaves the definition Retired")]
    public async Task The_definition_is_retired_and_audited()
    {
        var ct = TestContext.Current.CancellationToken;

        var before = await Operations.Definitions.GetAsync(TestNamespace, "job-wait-signal", ct);
        Assert.NotNull(before);
        Assert.Equal(ControlAction.Applied, (await RetireAsync("job-wait-signal", ct)).Action);

        var after = await Operations.Definitions.GetAsync(TestNamespace, "job-wait-signal", ct);
        Assert.NotNull(after);
        Assert.Equal(JobDefinitionStatusCode.Retired, after!.Status);
        Assert.True(after.Version > before!.Version);

        var evt = await Db.From<JobEvent>()
            .Where(e => e.DefinitionId == before.DefinitionId && e.EventCode == EventCode.JobDefinitionRetired)
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(evt);
        Assert.Null(evt!.JobId);
        Assert.Equal(ActorCode.Operator, evt.ActorCode);
        Assert.Equal(ActorKey, evt.ActorKey);
        Assert.Equal(Reason, evt.ReasonMessage);
    }

    [Fact(DisplayName = "A running attempt keeps its lease through the retire and still completes")]
    public async Task A_running_attempt_is_left_to_finish()
    {
        var ct = TestContext.Current.CancellationToken;
        var (leaseTtl, ns, workerId) = await DepsAsync(ct);

        var running = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        var claimed = Assert.Single(
            await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, workerId, leaseTtl, running.JobId, ct)
        );
        Assert.Equal(
            StartExecutionAction.Started,
            await Services
                .GetRequiredService<IExecutionStore>()
                .StartExecutionAsync(claimed.JobId, workerId, claimed.ExecutionNumber, claimed.Version, leaseTtl, ct)
        );

        Assert.Equal(ControlAction.Applied, (await RetireAsync("add-numbers", ct)).Action);

        var mid = await ReadJobAsync(running.JobId, ct);
        Assert.Equal(JobStatusCode.Executing, mid.Status);
        Assert.Equal(workerId, mid.LeasedByWorkerId);
        Assert.Equal(0, await CountEventsAsync(running.JobId, EventCode.JobCancelled, ct));

        var completion = await Services
            .GetRequiredService<IExecutionStore>()
            .CompleteExecutionAsync(
                new CompleteExecutionRequest(
                    JobId: claimed.JobId,
                    WorkerId: workerId,
                    ExpectedExecutionNumber: claimed.ExecutionNumber,
                    Outcome: ExecutionOutcome.Succeeded,
                    ResultFormatId: 0,
                    Result: ReadOnlyMemory<byte>.Empty
                ),
                ct
            );

        Assert.Equal(CompleteExecutionAction.Completed, completion.Action);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(running.JobId, ct)).Status);
    }

    [Fact(DisplayName = "A Suspended parent waiting on a child of the retired definition is released without a recovery pass")]
    public async Task A_parent_waiting_on_a_cancelled_child_is_released()
    {
        var ct = TestContext.Current.CancellationToken;

        var parent = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-parent-one", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        Assert.Equal(JobStatusCode.Suspended, (await ReadJobAsync(parent.JobId, ct)).Status);

        var child = Assert.Single(await Db.From<Job>().Where(j => j.ParentId == parent.JobId).ToListAsync(ct));

        Assert.Equal(ControlAction.Applied, (await RetireAsync("job-child-echo", ct)).Action);

        Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(child.Id, ct)).Status);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(parent.JobId, ct)).Status);

        var latch = Assert.Single(await ReadSignalsAsync(parent.JobId, ct));
        Assert.Equal($"sys.child.{child.Id}", latch.Name);
        Assert.Equal(JobCheckpointStatusCode.Set, latch.Status);
    }

    [Fact(DisplayName = "Enqueue against a retired definition is rejected with DefinitionRetired")]
    public async Task Enqueue_after_the_retire_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(ControlAction.Applied, (await RetireAsync("add-numbers", ct)).Action);

        var rejected = await Assert.ThrowsAsync<EnqueueRejectedException>(async () =>
            await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))), ct)
        );
        Assert.Equal(EnqueueRejectionReason.DefinitionRetired, rejected.Reason);
    }

    [Fact(DisplayName = "An unknown name is NotFound and a stale version is Rejected, and neither writes")]
    public async Task Unknown_is_not_found_and_a_stale_version_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await Operations.Definitions.GetAsync(TestNamespace, "add-numbers", ct);
        var eventsBefore = await CountDefinitionEventsAsync(before!.DefinitionId, ct);
        var parked = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))),
            ct
        );

        var missing = await Operations.Definitions.RetireAsync(TestNamespace, "no-such-job", 0, ActorKey, Reason, ct);
        Assert.Equal(ControlAction.NotFound, missing.Action);

        // The service answers NotFound before the store for an unknown name; an id the catalog never
        // assigned reaches the verb itself and must answer the same.
        var actor = new JobControlActor(ActorCode.Operator, ActorKey);
        var unknownId = await Services
            .GetRequiredService<IDefinitionStore>()
            .RetireDefinitionAsync(new RetireDefinitionCommand(int.MaxValue, 0, actor, Reason), ct);
        Assert.Equal(DefinitionOverrideAction.NotFound, unknownId.Action);
        Assert.Empty(unknownId.CancelledJobs);

        var stale = await Operations.Definitions.RetireAsync(TestNamespace, "add-numbers", 9999, ActorKey, Reason, ct);
        Assert.Equal(ControlAction.Rejected, stale.Action);

        // Neither call wrote: the row keeps its status and version, its parked job is still Ready, and
        // no definition-level event was recorded.
        var after = await Operations.Definitions.GetAsync(TestNamespace, "add-numbers", ct);
        Assert.Equal(JobDefinitionStatusCode.Active, after!.Status);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(JobStatusCode.Ready, await Jobs.GetStatusAsync(parked, ct));
        Assert.Equal(eventsBefore, await CountDefinitionEventsAsync(before.DefinitionId, ct));
    }

    [Fact(DisplayName = "Retiring an already retired definition is applied and writes no further events")]
    public async Task A_second_retire_is_applied_and_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;

        var parked = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);
        Assert.Equal(ControlAction.Applied, (await RetireAsync("job-wait-signal", ct)).Action);

        var definition = await Operations.Definitions.GetAsync(TestNamespace, "job-wait-signal", ct);
        var eventsBefore = await CountDefinitionEventsAsync(definition!.DefinitionId, ct);

        var second = await Operations.Definitions.RetireAsync(TestNamespace, "job-wait-signal", definition.Version, ActorKey, Reason, ct);
        Assert.Equal(ControlAction.Applied, second.Action);

        Assert.Equal(eventsBefore, await CountDefinitionEventsAsync(definition.DefinitionId, ct));
        Assert.Equal(1, await CountEventsAsync(parked.JobId, EventCode.JobCancelled, ct));
        Assert.Equal(definition.Version, (await Operations.Definitions.GetAsync(TestNamespace, "job-wait-signal", ct))!.Version);
    }

    [Fact(DisplayName = "An equal manifest generation reactivates a retired definition and an older one cannot")]
    public async Task Re_registration_reactivates_only_from_an_equal_or_newer_generation()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var descriptors = ImmutableArray.Create(TestJobsManifest.Descriptors.Descriptors.Single(d => d.JobName == "add-numbers"));

        Assert.Equal(ControlAction.Applied, (await RetireAsync("add-numbers", ct)).Action);

        await DefinitionTestOps.RegisterAsync(Services, ns, OlderGeneration, descriptors, ct);
        Assert.Equal(JobDefinitionStatusCode.Retired, (await Operations.Definitions.GetAsync(TestNamespace, "add-numbers", ct))!.Status);
        await Assert.ThrowsAsync<EnqueueRejectedException>(async () =>
            await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))), ct)
        );

        await DefinitionTestOps.RegisterAsync(Services, ns, Generation, descriptors, ct);
        Assert.Equal(JobDefinitionStatusCode.Active, (await Operations.Definitions.GetAsync(TestNamespace, "add-numbers", ct))!.Status);

        var accepted = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))),
            ct
        );
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(accepted.JobId, ct)).Status);
    }

    private async Task<DefinitionControlResult> RetireAsync(string jobName, CancellationToken ct)
    {
        var definition = await Operations.Definitions.GetAsync(TestNamespace, jobName, ct);
        Assert.NotNull(definition);
        return await Operations.Definitions.RetireAsync(TestNamespace, jobName, definition!.Version, ActorKey, Reason, ct);
    }

    private async Task<int> CountDefinitionEventsAsync(int definitionId, CancellationToken ct) =>
        await Db.From<JobEvent>().Where(e => e.DefinitionId == definitionId && e.JobId == null).CountAsync(ct);

    private async Task<(int LeaseTtl, int Ns, int WorkerId)> DepsAsync(CancellationToken ct)
    {
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var leaseTtl = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;
        var worker = await Db.From<JobWorker>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);
        return (leaseTtl, ns, worker!.Id);
    }
}
