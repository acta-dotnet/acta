using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// The job the rolled-out build adds and the rolled-back build has never heard of. Trivial on
/// purpose: what is under test is the claim, not the handler.
/// </summary>
internal static class RollingOnlyHandler
{
    public static Task Run(JobContext ctx, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// The rolled-out generation's manifest, hand-written because it is a second version of a manifest
/// the generator emits once per assembly. It keeps <c>add-numbers</c> (the same descriptor the old
/// generation carries, so the two deployments agree about it), adds <c>rolling-only</c>, and omits
/// <c>failures-audit-probe</c> - the three relationships a rolling deploy can have to a definition.
/// </summary>
public sealed class RollingGenerationBManifest : IJobManifest
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
                OutputType: null,
                InputPayloadFormat: JobPayloadFormat.None,
                OutputPayloadFormat: null,
                InvocationKind: JobInvocationKind.Task,
                RequiresJobContextParameter: true,
                RequiresCancellationToken: true,
                Priority: JobPriorityCode.Normal,
                MaxAttempts: 2,
                AuditLevel: JobAuditLevelCode.Audit,
                AlertProfile: AlertProfileCode.OnFailure,
                Invoker: static async (_, _, ctx, ct) =>
                {
                    await RollingOnlyHandler.Run(ctx, ct);
                    return new JobHandlerInvocationResult(false, null);
                },
                DeserializeInput: static (_, _) => new NoInput(),
                SerializeOutput: null
            )
            {
                CreateDefaultInput = static () => new NoInput(),
                SerializeInput = null,
            },
        ]);
}

/// <summary>
/// A rolling deploy run end to end against one namespace: the old generation (the test base's own
/// runtime, on <c>TestJobsManifest</c>) and the new one (<see cref="RollingGenerationBManifest"/>)
/// are live together, and then the deploy is rolled back to a fresh old-generation process. Claims
/// are by namespace, so each generation is free to claim the other's work; what the deploy relies on
/// is that a claim it cannot run is handed back budget-neutral and that the definition behind it is
/// excluded from that worker's later claims, so the window costs one bounce per definition per
/// worker rather than one per job per safety-poll interval.
/// <para>The catalog side of the same window: registering a manifest that omits a definition leaves
/// that definition Active, so a rolled-back deployment still runs its jobs with the policy it
/// registered. Clearing what the withdrawn generation left behind is the operator's retire verb.</para>
/// </summary>
[ConformanceSpec(
    "runtime.rolling-deploy",
    "Two generations share a namespace and the rollback still runs",
    Area = "Runtime",
    Contract = "A worker hands back a claim it has no handler for, stops claiming that definition, and leaves the catalog entry Active.",
    Arrange = "Two runtimes register different manifest generations into one namespace, and one job of each generation's definitions is enqueued.",
    Act = "The old generation ticks, then the new one, a rolled-back process takes over, and an operator retires the definition the fleet dropped.",
    Assert = "Each generation runs what it carries, the rollback still runs the omitted definition, and the retire cancels the stranded row and closes enqueue."
)]
public abstract class RollingDeploySpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const string SharedJob = RollingGenerationBManifest.SharedJob;

    private const string NewJob = RollingGenerationBManifest.NewJob;

    // Declared only by the old generation: what the rollback has to keep working.
    private const string OldOnlyJob = "failures-audit-probe";

    private static readonly DateTime GenerationA = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime GenerationB = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.Configure<JobsOptions>(o => o.ManifestGenerationUtc = GenerationA);
    }

    [Fact(
        DisplayName = "Two generations in one namespace hand back each other's work, learn it, and the rollback still runs the definition the new generation omitted"
    )]
    public async Task A_rolling_deploy_hands_work_back_and_survives_a_rollback()
    {
        var ct = TestContext.Current.CancellationToken;

        // --- 1. The new generation joins the namespace and registers its manifest.
        var newGeneration = BuildGenerationProvider<RollingGenerationBManifest>(GenerationB, "generation-b");
        var newRuntime = newGeneration.GetServices<WorkerRuntime>().Single();
        await newRuntime.InitializeAsync(ct);

        var shared = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, SharedJob, JobPayload.Json(new AddNumbers(2, 3))), ct);
        var newOnly = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, NewJob, JobPayload.None), ct);
        var oldOnly = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, OldOnlyJob, JobPayload.None), ct);

        // --- 2. The old generation drains what it can. The new generation's job is not its work.
        await TickUntilNothingClaimedAsync(Runtime, ct);

        var handedBack = await ReadJobAsync(newOnly.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, handedBack.Status);
        Assert.Equal((short)0, handedBack.FailureCount);
        Assert.Null(handedBack.LeasedByWorkerId);
        Assert.Contains(handedBack.DefinitionId, Runtime.UnsupportedDefinitionIdsSnapshot);

        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(shared.JobId, ct)).Status);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(oldOnly.JobId, ct)).Status);

        // Registering a manifest that omits a definition does not retire it: the old generation is
        // still deployed, and its jobs are still enqueueable and claimable.
        var oldOnlyDefinition = await ReadDefinitionAsync(OldOnlyJob, ct);
        Assert.Equal(JobDefinitionStatusCode.Active, oldOnlyDefinition.Status);

        // --- 3. The generation that carries the handler runs the handed-back job, no retry spent, and
        // hands back the other way: a job of the definition it omitted is not its work either.
        var oldOnlyAgain = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, OldOnlyJob, JobPayload.None), ct);
        await RunUntilSucceededAsync(newRuntime, newOnly.JobId, ct);
        Assert.Equal((short)0, (await ReadJobAsync(newOnly.JobId, ct)).FailureCount);
        await TickUntilNothingClaimedAsync(newRuntime, ct);

        var handedBackToOld = await ReadJobAsync(oldOnlyAgain.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, handedBackToOld.Status);
        Assert.Equal((short)0, handedBackToOld.FailureCount);
        Assert.Null(handedBackToOld.LeasedByWorkerId);
        Assert.Contains(handedBackToOld.DefinitionId, newRuntime.UnsupportedDefinitionIdsSnapshot);

        await RunUntilSucceededAsync(Runtime, oldOnlyAgain.JobId, ct);
        Assert.Equal((short)0, (await ReadJobAsync(oldOnlyAgain.JobId, ct)).FailureCount);

        // --- 4. Rollback: the new generation is gone and a fresh old-generation process takes over,
        // with one job of each definition waiting for it.
        await newGeneration.DisposeAsync();
        var strandedNew = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, NewJob, JobPayload.None), ct);
        var rolledBackOld = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, OldOnlyJob, JobPayload.None), ct);

        await using var rollback = BuildGenerationProvider<TestJobsManifest>(GenerationA, "generation-a-rollback");
        var rollbackRuntime = rollback.GetServices<WorkerRuntime>().Single();
        await rollbackRuntime.InitializeAsync(ct);

        await TickUntilNothingClaimedAsync(rollbackRuntime, ct);

        // The definition the new generation never registered is untouched by it, so the rolled-back
        // process runs its job under exactly the policy it registered in the first place.
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(rolledBackOld.JobId, ct)).Status);
        var afterRollback = await ReadDefinitionAsync(OldOnlyJob, ct);
        Assert.Equal(JobDefinitionStatusCode.Active, afterRollback.Status);
        Assert.Equal(oldOnlyDefinition.DefinitionHash, afterRollback.DefinitionHash);
        Assert.Equal(oldOnlyDefinition.ManifestGenerationAtUtc, afterRollback.ManifestGenerationAtUtc);
        Assert.Equal(oldOnlyDefinition.MaxAttemptsEffective, afterRollback.MaxAttemptsEffective);
        Assert.Equal(oldOnlyDefinition.BackoffEffective, afterRollback.BackoffEffective);

        // The job only the withdrawn generation could run waits where it is: Ready, budget-neutral,
        // and excluded from the process that cannot run it, rather than bouncing once a second.
        var stranded = await ReadJobAsync(strandedNew.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, stranded.Status);
        Assert.Equal((short)0, stranded.FailureCount);
        Assert.Contains(stranded.DefinitionId, rollbackRuntime.UnsupportedDefinitionIdsSnapshot);

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
        Assert.Equal(JobEventReasonCode.JobDefinitionRetired, cancelEvent.ReasonCode);

        var rejected = await Assert.ThrowsAsync<EnqueueRejectedException>(async () =>
            await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, NewJob, JobPayload.None), ct)
        );
        Assert.Equal(EnqueueRejectionReason.DefinitionRetired, rejected.Reason);
    }

    /// <summary>
    /// One generation's process: its own DI container, its own worker row, its own manifest and
    /// generation stamp, against the shared schema and namespace.
    /// </summary>
    private ServiceProvider BuildGenerationProvider<TManifest>(DateTime generationUtc, string tag)
        where TManifest : class, IJobManifest
    {
        var services = new ServiceCollection();
        services.UseActa(j =>
        {
            Fixture.ApplyProvider(j, Schema.SchemaName);
            j.Run<TManifest>(TestNamespace, ownerTeam: "test", description: GetType().FullName + ":" + tag);
        });
        services.Configure<JobsOptions>(o =>
        {
            o.RegisterSystemJobs = false;
            o.ManifestGenerationUtc = generationUtc;
        });
        return services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>
    /// Ticks the namespace-level claim (the path the exclusion filters) until one tick claims
    /// nothing. Bounded, so a namespace that keeps handing out work fails the test rather than
    /// hanging it.
    /// </summary>
    private static async Task TickUntilNothingClaimedAsync(WorkerRuntime runtime, CancellationToken ct)
    {
        var jobNamespace = runtime.RegisteredNamespaceIds.Keys.Single();
        for (var tick = 0; tick < 32; tick++)
        {
            if (await runtime.RunOnceAsync(jobNamespace, ct) == RunOnceOutcome.NothingClaimed)
            {
                return;
            }
        }

        Assert.Fail("the namespace still claimed work after 32 ticks.");
    }

    /// <summary>
    /// Ticks until <paramref name="jobId"/> is Succeeded. A job handed back by another generation is
    /// re-armed a safety-poll interval out, so the first ticks legitimately claim nothing.
    /// </summary>
    private async Task RunUntilSucceededAsync(WorkerRuntime runtime, long jobId, CancellationToken ct)
    {
        var jobNamespace = runtime.RegisteredNamespaceIds.Keys.Single();
        var deadline = DateTime.UtcNow + SpecWaits.Converge;
        while (DateTime.UtcNow < deadline)
        {
            await runtime.RunOnceAsync(jobNamespace, ct);
            if ((await ReadJobAsync(jobId, ct)).Status == JobStatusCode.Succeeded)
            {
                return;
            }
            await Task.Delay(50, ct);
        }

        Assert.Fail($"job {jobId} never reached Succeeded: it is {(await ReadJobAsync(jobId, ct)).Status}.");
    }

    private async Task<JobDefinition> ReadDefinitionAsync(string jobName, CancellationToken ct)
    {
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var definition = await Db.From<JobDefinition>().Where(d => d.NamespaceId == ns && d.Name == jobName).SingleOrDefaultAsync(ct);
        Assert.NotNull(definition);
        return definition!;
    }
}
