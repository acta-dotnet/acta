using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// The job the rolled-out build adds and the rolled-back build has never heard of. Trivial on
/// purpose: what is under test is the claim, not the handler.
/// </summary>
internal static class RollingOnlyHandler
{
    public static Task Run(JobContext ctx, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// The rolled-out build's manifest, hand-written because it is a second version of a manifest
/// the generator emits once per assembly. It keeps <c>add-numbers</c> (the same descriptor the old
/// build carries, so the two deployments agree about it), adds <c>rolling-only</c>, and omits
/// <c>failures-audit-probe</c> - the three relationships a rolling deploy can have to a definition.
/// </summary>
public sealed class RollingNextBuildManifest : IJobManifest
{
    public const string SharedJob = "add-numbers";

    public const string NewJob = "rolling-only";

    public static JobDescriptorManifest Descriptors { get; } =
        new([
            TestJobsManifest.Descriptors.Descriptors.Single(d => d.JobName == SharedJob),
            new JobDescriptor(
                JobName: NewJob,
                HandlerType: typeof(RollingOnlyHandler),
                MethodName: nameof(RollingOnlyHandler.Run),
                InputType: typeof(NoInput),
                ResultType: null,
                InputPayloadFormat: JobPayloadFormat.None,
                ResultPayloadFormat: null,
                Priority: JobPriorityCode.Normal,
                MaxAttempts: 2,
                EventRecording: EventRecordingCode.Audit,
                AlertProfile: AlertProfileCode.OnFailure,
                FiringFailurePolicy: FiringFailurePolicyCode.Continue,
                ScheduledDelivery: ScheduledDeliveryCode.Permanent,
                Invoker: static async (_, _, ctx, ct) =>
                {
                    await RollingOnlyHandler.Run(ctx, ct);
                    return new JobHandlerOutput(false, null);
                },
                DeserializeInput: static (_, _) => new NoInput(),
                SerializeResult: null
            )
            {
                CreateDefaultInput = static () => new NoInput(),
                SerializeInput = null,
            },
        ]);
}

/// <summary>
/// A rolling deploy run end to end against one namespace: the old build (the test base's own
/// runtime, on <c>TestJobsManifest</c>) and the new one (<see cref="RollingNextBuildManifest"/>)
/// are live together, and then the deploy is rolled back to a fresh old-build process. Claims
/// are by namespace, so each build is free to claim the other's work; what the deploy relies on
/// is that a claim it cannot run is handed back budget-neutral and that the definition behind it is
/// excluded from that worker's later claims, so the window costs one bounce per definition per
/// worker rather than one per job per safety-poll interval.
/// <para>The catalog side of the same window: deploying a manifest that omits a definition leaves
/// that definition Active, so a rolled-back build still runs its jobs with the policy it
/// deployed. Clearing what the withdrawn build left behind is the operator's retire verb.</para>
/// </summary>
[ConformanceSpec(
    "runtime.rolling-deploy",
    "Two builds share a namespace and the rollback still runs",
    Area = "Runtime",
    Contract = "A worker hands back a claim it has no handler for and stops claiming that definition, and a deploy that omits a definition leaves it Active."
)]
public sealed class RollingDeploySpec : ActaRuntimeTestBase<ConformanceFixture, TestJobsManifest>
{
    private const string SharedJob = RollingNextBuildManifest.SharedJob;

    private const string NewJob = RollingNextBuildManifest.NewJob;

    // Declared only by the old build: what the rollback has to keep working.
    private const string OldOnlyJob = "failures-audit-probe";

    [Fact(
        DisplayName = "Two builds in one namespace hand back each other's work, learn it, and the rollback still runs the definition the new build omitted"
    )]
    public async Task A_rolling_deploy_hands_work_back_and_survives_a_rollback()
    {
        var ct = TestContext.Current.CancellationToken;

        // --- 1. The new build deploys its manifest and joins the namespace.
        var newBuild = BuildProvider<RollingNextBuildManifest>("new-build");
        var newRuntime = newBuild.GetServices<WorkerRuntime>().Single();
        await CatalogTestOps.DeployAndStartAsync(newBuild, newRuntime, ct);

        var shared = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, SharedJob, JobPayload.Json(new AddNumbers(2, 3))), ct);
        var newOnly = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, NewJob, JobPayload.None), ct);
        var oldOnly = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, OldOnlyJob, JobPayload.None), ct);

        // --- 2. The old build drains what it can. The new build's job is not its work.
        var newDefinitionId = (await ReadJobAsync(newOnly.JobId, ct)).DefinitionId;
        await TickUntilAsync(
            Runtime,
            TestNamespace,
            async () =>
                Runtime.UnsupportedDefinitionIdsSnapshot.Contains(newDefinitionId)
                && (await ReadJobAsync(shared.JobId, ct)).Status == JobStatusCode.Succeeded
                && (await ReadJobAsync(oldOnly.JobId, ct)).Status == JobStatusCode.Succeeded,
            ct
        );
        await AssertPassedOverAsync(Runtime, newOnly.JobId, ct);

        var handedBack = await ReadJobAsync(newOnly.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, handedBack.Status);
        Assert.Equal(0, handedBack.FailureCount);
        Assert.Null(handedBack.LeasedByWorkerId);

        // Deploying a manifest that omits a definition does not retire it: the old build is
        // still running, and its jobs are still enqueueable and claimable.
        var oldOnlyDefinition = await ReadDefinitionAsync(OldOnlyJob, ct);
        Assert.Equal(DefinitionStatusCode.Active, oldOnlyDefinition.Status);

        // --- 3. The build that carries the handler runs the handed-back job, no retry spent, and
        // hands back the other way: a job of the definition it omitted is not its work either.
        var oldOnlyAgain = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, OldOnlyJob, JobPayload.None), ct);
        await RunUntilSucceededAsync(newRuntime, newOnly.JobId, ct);
        Assert.Equal(0, (await ReadJobAsync(newOnly.JobId, ct)).FailureCount);
        var oldDefinitionId = (await ReadJobAsync(oldOnlyAgain.JobId, ct)).DefinitionId;
        await TickUntilAsync(
            newRuntime,
            TestNamespace,
            () => Task.FromResult(newRuntime.UnsupportedDefinitionIdsSnapshot.Contains(oldDefinitionId)),
            ct
        );
        await AssertPassedOverAsync(newRuntime, oldOnlyAgain.JobId, ct);

        var handedBackToOld = await ReadJobAsync(oldOnlyAgain.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, handedBackToOld.Status);
        Assert.Equal(0, handedBackToOld.FailureCount);
        Assert.Null(handedBackToOld.LeasedByWorkerId);

        await RunUntilSucceededAsync(Runtime, oldOnlyAgain.JobId, ct);
        Assert.Equal(0, (await ReadJobAsync(oldOnlyAgain.JobId, ct)).FailureCount);

        // --- 4. Rollback: the new build is gone and a fresh old-build process takes over,
        // with one job of each definition waiting for it.
        await newBuild.DisposeAsync();
        var strandedNew = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, NewJob, JobPayload.None), ct);
        var rolledBackOld = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, OldOnlyJob, JobPayload.None), ct);

        await using var rollback = BuildProvider<TestJobsManifest>("old-build-rollback");
        var rollbackRuntime = rollback.GetServices<WorkerRuntime>().Single();
        await CatalogTestOps.DeployAndStartAsync(rollback, rollbackRuntime, ct);

        await TickUntilAsync(
            rollbackRuntime,
            TestNamespace,
            async () =>
                rollbackRuntime.UnsupportedDefinitionIdsSnapshot.Contains(newDefinitionId)
                && (await ReadJobAsync(rolledBackOld.JobId, ct)).Status == JobStatusCode.Succeeded,
            ct
        );
        await AssertPassedOverAsync(rollbackRuntime, strandedNew.JobId, ct);

        // The definition the new build's deploy omitted is untouched by that deploy, so the rolled-back
        // process runs its job under exactly the policy the old build deployed in the first place.
        var afterRollback = await ReadDefinitionAsync(OldOnlyJob, ct);
        Assert.Equal(DefinitionStatusCode.Active, afterRollback.Status);
        Assert.Equal(oldOnlyDefinition.DefinitionHash, afterRollback.DefinitionHash);
        Assert.Equal(oldOnlyDefinition.MaxAttemptsEffective, afterRollback.MaxAttemptsEffective);
        Assert.Equal(oldOnlyDefinition.BackoffEffective, afterRollback.BackoffEffective);

        // The job only the withdrawn build could run waits where it is: Ready, budget-neutral,
        // and excluded from the process that cannot run it, rather than bouncing once a second.
        var stranded = await ReadJobAsync(strandedNew.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, stranded.Status);
        Assert.Equal(0, stranded.FailureCount);

        // --- 5. The stranded row waits until an operator says so. Retiring the definition the fleet
        // no longer carries is what clears it, and it closes the definition to new work too.
        var newOnlyDefinition = await ReadDefinitionAsync(NewJob, ct);
        var retire = await Operations.Definitions.RetireAsync(
            TestNamespace,
            NewJob,
            newOnlyDefinition.Version,
            "rollback-operator",
            "rolled back",
            ct
        );
        Assert.Equal(ControlAction.Applied, retire.Action);

        Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(strandedNew.JobId, ct)).Status);
        var cancelEvent = await ReadLatestEventAsync(strandedNew.JobId, EventCode.JobCancelled, ct);
        Assert.Equal(EventReason.DefinitionRetired, EventDetail(cancelEvent, "reason"));

        var rejected = await Assert.ThrowsAsync<EnqueueRejectedException>(async () =>
            await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, NewJob, JobPayload.None), ct)
        );
        Assert.Equal(EnqueueRejectionReason.DefinitionRetired, rejected.Reason);
    }

    /// <summary>
    /// Makes <paramref name="jobId"/> due and has <paramref name="runtime"/> tick once: the exclusion it learned passes over
    /// the row, where a claim that ignored it would take and bounce it.
    /// </summary>
    private async Task AssertPassedOverAsync(WorkerRuntime runtime, long jobId, CancellationToken ct)
    {
        await ChaosSpecHelpers.SetReadyAsync(Db, jobId, ct);
        Assert.Equal(RunOnceStatus.NothingClaimed, await runtime.RunOnceAsync(TestNamespace, ct));
    }

    /// <summary>
    /// Ticks until <paramref name="jobId"/> is Succeeded. A job handed back by another build is
    /// re-armed a safety-poll interval out, so the first ticks legitimately claim nothing.
    /// </summary>
    private Task RunUntilSucceededAsync(WorkerRuntime runtime, long jobId, CancellationToken ct) =>
        TickUntilAsync(runtime, TestNamespace, async () => (await ReadJobAsync(jobId, ct)).Status == JobStatusCode.Succeeded, ct);

    private async Task<DefinitionEntity> ReadDefinitionAsync(string jobName, CancellationToken ct)
    {
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var definition = await Db.From<DefinitionEntity>()
            .Where(d => d.NamespaceId == ns && d.DefinitionName == jobName)
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(definition);
        return definition!;
    }
}
