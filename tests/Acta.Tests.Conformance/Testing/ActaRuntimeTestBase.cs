using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Workers;
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
    /// <c>sys.recovery</c> / <c>sys.retention</c>) into this test's namespace. Default <c>false</c>:
    /// tests drive their own enqueued probe, and a due framework slot would otherwise race
    /// <see cref="WorkerRuntime.RunOnceAsync(string, System.Threading.CancellationToken)"/> for the
    /// claim. Specs that assert system-job behavior override to <c>true</c>.
    /// </summary>
    protected virtual bool RegisterSystemJobs => false;

    /// <summary>
    /// When <c>true</c> (default), the manifest's seeded schedule slots are parked a day out after
    /// <c>InitializeAsync</c>. Slots seed <c>next_run_at_utc</c> at the next cron boundary, so any
    /// test that straddles that instant would see them become claimable and contaminate
    /// namespace-wide claim, drain, and overview assertions. Schedule specs that assert real
    /// firing/cursor behavior override to <c>false</c>.
    /// </summary>
    protected virtual bool ParkScheduleSlots => true;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        services.UseActa(j =>
        {
            Fixture.ApplyProvider(j, Schema.SchemaName);
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
        var ct = TestContext.Current.CancellationToken;
        await Runtime.InitializeAsync(ct);

        if (ParkScheduleSlots)
        {
            // At this point the namespace's only Ready rows are the seeded schedule slots.
            var parked = DateTime.UtcNow.AddDays(1);
            var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
            // A non-Orphaned status matches ix_schedules_namespace_next's filter so SQL Server seeks
            // instead of scanning pk_schedules into unrelated fixtures' purge locks.
            await Db.From<JobSchedule>()
                .Where(s => s.NamespaceId == ns && s.Status != ScheduleStatusCode.Orphaned)
                .UpdateOnlyAsync(() => new JobSchedule { NextRunAtUtc = parked }, ct);
            await Db.From<JobRuntime>()
                .Where(r => r.NamespaceId == ns && r.Status == JobStatusCode.Ready)
                .UpdateOnlyAsync(() => new JobRuntime { NextRunAtUtc = parked }, ct);
        }
    }

    /// <summary>
    /// Enqueues <paramref name="input"/> as <paramref name="jobName"/> in the test namespace, drives one
    /// runtime tick to completion, and returns the enqueue outcome (internal id and public ref).
    /// </summary>
    protected async Task<JobEnqueueOutcome> EnqueueAndRunAsync(string jobName, object input, CancellationToken ct)
    {
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(input);
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, jobName, payload, null, null, null), ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        return enqueued;
    }
}
