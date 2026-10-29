using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Base for WorkerRuntime-level specs. Registers <typeparamref name="TManifest"/> under the per-test
/// namespace via <c>IActaBuilder.Run&lt;TManifest&gt;</c>, then calls
/// <see cref="WorkerRuntime.InitializeAsync"/> in <see cref="AfterInitializeAsync"/>. The poll loop
/// (<see cref="WorkerRuntime.RunLoopAsync"/>) is opt-in - registration tests only need
/// <see cref="WorkerRuntime.InitializeAsync"/>; execution specs call <see cref="WorkerRuntime.RunOnceAsync"/>
/// directly to drive one tick.
/// </summary>
public abstract class ActaRuntimeTestBase<TFixture, TManifest> : ActaTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
    where TManifest : class, IJobManifest
{
    internal WorkerRuntime Runtime { get; private set; } = null!;

    protected IJobs Jobs => Services.GetRequiredService<IJobs>();

    protected IActaOperations Operations => Services.GetRequiredService<IActaOperations>();

    /// <summary>
    /// When <c>true</c> (default), the test base calls <c>j.Run&lt;TManifest&gt;()</c> so the runtime writes
    /// a <c>workers</c> row on <c>InitializeAsync</c> and <c>RunOnceAsync</c> is callable.
    /// Override to <c>false</c> in enqueue-only specs.
    /// </summary>
    protected virtual bool RunAsWorker => true;

    /// <summary>
    /// Whether <c>InitializeAsync</c> registers the system recurring slots (<c>sys.alerts</c> /
    /// <c>sys.retention</c>) into this test's namespace. Default <c>false</c>:
    /// tests drive their own enqueued probe, and a due framework slot would otherwise race
    /// <see cref="WorkerRuntime.RunOnceAsync(string, System.Threading.CancellationToken)"/> for the
    /// claim. Specs that assert system-job behavior override to <c>true</c>.
    /// </summary>
    protected virtual bool RegisterSystemJobs => false;

    /// <summary>
    /// When <c>true</c> (default), the manifest's seeded schedules are parked a day out after
    /// <c>InitializeAsync</c>. Schedules seed their cursor and next evaluation at the next cron boundary, so any
    /// test that straddles that instant would see the admitter fire their slots and contaminate
    /// namespace-wide claim, drain, and overview assertions. Schedule specs that assert real
    /// firing/cursor behavior override to <c>false</c>.
    /// </summary>
    protected virtual bool ParkScheduleSlots => true;

    /// <summary>A spec's own registrations on the builder, such as the calendars its schedules bind; nothing by default.</summary>
    protected virtual void ConfigureActa(IActaBuilder acta) { }

    /// <summary>What a spec puts in the database before its worker registers, such as the catalog calendars its schedules bind.</summary>
    protected virtual ValueTask BeforeWorkerStartAsync(CancellationToken ct) => ValueTask.CompletedTask;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        services.UseActa(j =>
        {
            Fixture.ApplyProvider(j, Schema.SchemaName);
            ConfigureActa(j);
            if (RunAsWorker)
            {
                j.Run<TManifest>(testNamespace, ownerTeam: "test", description: GetType().FullName);
            }
        });
        services.Configure<JobsOptions>(o => o.RegisterSystemJobs = RegisterSystemJobs);
    }

    protected override async ValueTask AfterInitializeAsync()
    {
        if (!RunAsWorker)
        {
            return;
        }
        Runtime = Services.GetServices<WorkerRuntime>().Single();
        // The base resolves its runtime here rather than through ActaTestHost, so the probe that stops
        // the run-once helper waiting out its budget on an unclaimable row is attached here as well.
        Runtime.AttachClaimabilityProbe(Jobs);
        var ct = TestContext.Current.CancellationToken;
        await BeforeWorkerStartAsync(ct);
        await Runtime.InitializeAsync(ct);

        var namespaceId = Runtime.RegisteredNamespaceIds[TestNamespace];
        var slots = (
            await Db.From<JobEntity>().Where(j => j.NamespaceId == namespaceId && j.Kind == JobKindCode.PermanentSlot).ToListAsync(ct)
        )
            .Select(j => j.Id)
            .ToList();
        if (ParkScheduleSlots && slots.Count > 0)
        {
            // Addressed by job id, so every write seeks: a filter on namespace and status reaches no
            // filtered index through the test query layer's parameters, and scans the whole shared schema.
            var parked = DateTime.UtcNow.AddDays(1);
            var dialect = Services.GetRequiredService<ISqlDialect>();
            var slotKeys = slots.ConvertAll(id => (long?)id);
            var scheduleIds = (await Db.From<ScheduleEntity>().Where(s => slotKeys.Contains(s.JobId)).ToListAsync(ct))
                .Select(s => s.Id)
                .ToList();
            await RetryTransientConflictAsync(
                dialect,
                () =>
                    Db.From<ScheduleStateEntity>()
                        .Where(s => scheduleIds.Contains(s.Id) && s.NextOccurrenceAtUtc != null)
                        .UpdateOnlyAsync(() => new ScheduleStateEntity { NextOccurrenceAtUtc = parked, NextEvaluationAtUtc = parked }, ct),
                ct
            );
            await RetryTransientConflictAsync(
                dialect,
                () =>
                    Db.From<JobStateEntity>()
                        .Where(r => slots.Contains(r.Id) && r.Status == JobStatusCode.Ready)
                        .UpdateOnlyAsync(() => new JobStateEntity { NextRunAtUtc = parked }, ct),
                ct
            );
        }
    }

    /// <summary>
    /// Repeats a testing-layer write the database aborted as a deadlock victim, through the same bounded,
    /// jittered retry the stores use. The testing query layer issues each write once, and every class in
    /// the suite parks its slots at the same instant on one shared schema, which on a small SQL Server
    /// picks a victim among them; the victim's transaction is rolled back whole, so repeating is safe.
    /// </summary>
    private static Task RetryTransientConflictAsync(ISqlDialect dialect, Func<Task> write, CancellationToken ct) =>
        DeadlockRetry.RunAsync(
            async _ =>
            {
                await write();
                return true;
            },
            dialect.IsTransientConflict,
            maxAttempts: 8,
            ct
        );

    /// <summary>
    /// Another deployment generation's process for a rolling-deploy spec: its own DI container, its own
    /// worker row, its own manifest and generation stamp, against the shared schema and namespace.
    /// </summary>
    protected ServiceProvider BuildGenerationProvider<TGenerationManifest>(DateTime generationUtc, string tag)
        where TGenerationManifest : class, IJobManifest
    {
        var services = new ServiceCollection();
        services.UseActa(j =>
        {
            Fixture.ApplyProvider(j, Schema.SchemaName);
            j.Run<TGenerationManifest>(TestNamespace, ownerTeam: "test", description: GetType().FullName + ":" + tag);
        });
        services.Configure<JobsOptions>(o =>
        {
            o.RegisterSystemJobs = false;
            o.ManifestGenerationUtc = generationUtc;
        });
        return services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>
    /// Enqueues <paramref name="input"/> as <paramref name="jobName"/> in the test namespace, drives one
    /// runtime tick to completion, and returns the enqueue outcome (internal id and public ref).
    /// </summary>
    protected async Task<EnqueueJobResponse> EnqueueAndRunAsync(string jobName, object input, CancellationToken ct)
    {
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(input);
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, jobName, payload, null, null, null), ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        return enqueued;
    }
}
