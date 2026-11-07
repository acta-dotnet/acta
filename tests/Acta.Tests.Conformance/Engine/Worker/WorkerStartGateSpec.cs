using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Engine.Catalog;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// What a worker start needs to find: its namespace, its own version among the namespace's deployed ones when a deploy
/// verb has recorded any, and a definition for every job its build declares. Each spec brings up builds as their own
/// containers, with their own version and manifest, the way separate processes share a database. A start under
/// <c>MigrateOnStartup</c> deploys without recording, and <c>jobs debug</c> starts on whatever is stored.
/// </summary>
[ConformanceSpec(
    "worker.start-gate",
    "A worker starts only where its build's jobs are deployed",
    Area = "Workers",
    Contract = "A worker refuses an absent namespace and, outside debug, a recorded namespace its version has not deployed in and a declared job with no definition."
)]
public sealed class WorkerStartGateSpec : ActaStorageTestBase<ConformanceFixture>
{
    private const string First = "1.0.0+b81d04e";
    private const string Second = "1.1.0+5c0ffee";

    private static int FirstAttempts =>
        DeployFirstManifest.Descriptors.Descriptors.Single(d => d.JobName == DeployFirstManifest.ChangedJob).MaxAttempts;

    /// <summary>One build as its own process would be: its manifest in <paramref name="ns"/>, running as <paramref name="version"/>.</summary>
    private ServiceProvider Build<TManifest>(string ns, string version)
        where TManifest : class, IJobManifest
    {
        var services = new ServiceCollection();
        services.UseActa(j =>
        {
            Fixture.ApplyProvider(j, Schema.SchemaName);
            j.Run<TManifest>(ns, ownerTeam: "billing");
        });
        services.Configure<JobsOptions>(o =>
        {
            o.DeploymentVersion = version;
            o.RunSystemJobs = false;
        });
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static WorkerRuntime Runtime(ServiceProvider build) => build.GetServices<WorkerRuntime>().Single();

    private static Task DeployRecordedAsync(ServiceProvider build, CancellationToken ct) =>
        build.GetRequiredService<CatalogDeployer>().DeployAsync(force: false, ct);

    private static Task DeployUnrecordedAsync(ServiceProvider build, CancellationToken ct) =>
        build.GetRequiredService<CatalogDeployer>().DeployUnrecordedAsync(ct);

    private async Task<NamespaceEntity?> ReadNamespaceAsync(string ns, CancellationToken ct) =>
        await Db.From<NamespaceEntity>().Where(n => n.NamespaceName == ns).SingleOrDefaultAsync(ct);

    private async Task<Dictionary<string, DefinitionEntity>> ReadDefinitionsAsync(string ns, CancellationToken ct)
    {
        var namespaceId = (await ReadNamespaceAsync(ns, ct))!.Id;
        return (await Db.From<DefinitionEntity>().Where(d => d.NamespaceId == namespaceId).ToListAsync(ct)).ToDictionary(d =>
            d.DefinitionName
        );
    }

    private async Task<IReadOnlyList<WorkerEntity>> ReadWorkersAsync(string ns, CancellationToken ct)
    {
        var namespaceId = (await ReadNamespaceAsync(ns, ct))!.Id;
        return await Db.From<WorkerEntity>().Where(w => w.NamespaceId == namespaceId).ToListAsync(ct);
    }

    [Fact(DisplayName = "A worker refuses a namespace nothing was deployed in, names jobs deploy, and creates nothing")]
    public async Task A_worker_refuses_a_namespace_nothing_was_deployed_in()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Unique("gate-absent");
        await using var build = Build<DeployFirstManifest>(ns, First);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Runtime(build).InitializeAsync(ct));

        Assert.Contains($"Worker namespace '{ns}' cannot start: nothing has been deployed there", ex.Message, StringComparison.Ordinal);
        Assert.Contains("jobs deploy", ex.Message, StringComparison.Ordinal);
        Assert.Null(await ReadNamespaceAsync(ns, ct));
    }

    [Fact(
        DisplayName = "A worker whose build has not deployed refuses a recorded namespace and leaves no worker row, and starts once it deploys"
    )]
    public async Task A_worker_whose_build_has_not_deployed_refuses_a_recorded_namespace()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Unique("gate-recorded");
        await using var deployed = Build<DeployFirstManifest>(ns, First);
        await DeployRecordedAsync(deployed, ct);
        await using var local = Build<DeployFirstManifest>(ns, Second);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Runtime(local).InitializeAsync(ct));

        Assert.Contains($"it is deployed at version '{First}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"this build ('{Second}') has not deployed there", ex.Message, StringComparison.Ordinal);
        Assert.Contains("jobs deploy", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await ReadWorkersAsync(ns, ct));

        await DeployRecordedAsync(local, ct);
        await Runtime(local).InitializeAsync(ct);

        Assert.Equal(Second, Assert.Single(await ReadWorkersAsync(ns, ct)).DeploymentVersion);
    }

    [Fact(DisplayName = "A worker of any version starts in a namespace no deploy verb has recorded")]
    public async Task A_worker_of_any_version_starts_in_an_unrecorded_namespace()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Unique("gate-open");
        await using var first = Build<DeployFirstManifest>(ns, First);
        await DeployUnrecordedAsync(first, ct);
        await using var other = Build<DeployFirstManifest>(ns, Second);

        await Runtime(other).InitializeAsync(ct);

        Assert.Null((await ReadNamespaceAsync(ns, ct))!.DeploymentVersion);
        Assert.Equal(Second, Assert.Single(await ReadWorkersAsync(ns, ct)).DeploymentVersion);
    }

    [Fact(DisplayName = "An earlier deployed build starts beside the current one and runs a job that differs under the stored policy")]
    public async Task An_earlier_deployed_build_starts_and_runs_under_the_stored_policy()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Unique("gate-earlier");
        await using var earlier = Build<DeployFirstManifest>(ns, First);
        await DeployRecordedAsync(earlier, ct);
        await using var current = Build<DeploySecondManifest>(ns, Second);
        await DeployRecordedAsync(current, ct);
        var deployed = await ReadDefinitionsAsync(ns, ct);

        var runtime = Runtime(earlier);
        await runtime.InitializeAsync(ct);

        // The earlier build declares one attempt fewer; its worker runs the job with the deployed number.
        Assert.True(runtime.TryGetDefinitionId(DeployFirstManifest.ChangedJob, out var definitionId));
        Assert.Equal(FirstAttempts + 1, runtime.Descriptors[definitionId].MaxAttempts);
        Assert.All(await ReadDefinitionsAsync(ns, ct), d => Assert.Equal(deployed[d.Key].Version, d.Value.Version));
        Assert.Equal(Second, (await ReadNamespaceAsync(ns, ct))!.DeploymentVersion);
    }

    [Fact(DisplayName = "A worker refuses to start when its build declares a job with no definition, and names the job")]
    public async Task A_worker_refuses_when_a_declared_job_has_no_definition()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Unique("gate-missing");
        await using var deployed = Build<DeployFirstManifest>(ns, First);
        await DeployUnrecordedAsync(deployed, ct);
        await using var wider = Build<DeploySecondManifest>(ns, First);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Runtime(wider).InitializeAsync(ct));

        Assert.Contains(
            $"this build declares jobs that are not deployed there ({DeploySecondManifest.AddedJob})",
            ex.Message,
            StringComparison.Ordinal
        );
        Assert.Contains("jobs deploy", ex.Message, StringComparison.Ordinal);

        // The row written before the catalog was read is left Stopped, with both events: no crash for the sweep to find.
        var worker = Assert.Single(await ReadWorkersAsync(ns, ct));
        Assert.Equal(WorkerStatusCode.Stopped, worker.Status);
        var events = await Db.From<EventEntity>().Where(e => e.EntityCode == EntityCode.Worker && e.EntityId == worker.Id).ToListAsync(ct);
        Assert.Equal([EventCode.WorkerStarted, EventCode.WorkerStopped], events.OrderBy(e => e.Id).Select(e => e.EventCode));
    }

    [Fact(
        DisplayName = "A start under MigrateOnStartup deploys a namespace no verb has recorded, records nothing, and leaves a recorded one as it is"
    )]
    public async Task A_startup_deploy_writes_an_unrecorded_namespace_and_leaves_a_recorded_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Unique("gate-startup");
        await using var first = Build<DeployFirstManifest>(ns, First);

        await DeployUnrecordedAsync(first, ct);

        var open = await ReadNamespaceAsync(ns, ct);
        Assert.Null(open!.DeploymentVersion);
        Assert.Equal("billing", open.OwnerTeam);
        Assert.Equal(3, (await ReadDefinitionsAsync(ns, ct)).Count);
        Assert.Equal(0, await Db.From<DeploymentEntity>().Where(d => d.NamespaceId == open.Id).CountAsync(ct));
        Assert.Equal(
            0,
            await Db.From<EventEntity>().Where(e => e.NamespaceId == open.Id && e.EventCode == EventCode.NamespaceDeployed).CountAsync(ct)
        );

        await DeployRecordedAsync(first, ct);
        var recorded = await ReadDefinitionsAsync(ns, ct);
        await using var local = Build<DeploySecondManifest>(ns, Second);

        await DeployUnrecordedAsync(local, ct);

        // The local build's changed job and its added one are not there: the namespace is a deploy verb's.
        var after = await ReadDefinitionsAsync(ns, ct);
        Assert.Equal(recorded.Keys.Order(), after.Keys.Order());
        Assert.All(after, d => Assert.Equal(recorded[d.Key].Version, d.Value.Version));
        Assert.Equal(First, (await ReadNamespaceAsync(ns, ct))!.DeploymentVersion);
    }

    [Fact(
        DisplayName = "A debug start runs on what is stored: from a build that has not deployed, declaring a job with no definition, writing none"
    )]
    public async Task A_debug_start_runs_on_what_is_stored_and_writes_no_definition()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Unique("gate-debug");
        await using var deployed = Build<DeployFirstManifest>(ns, First);
        await DeployRecordedAsync(deployed, ct);
        var stored = await ReadDefinitionsAsync(ns, ct);
        await using var local = Build<DeploySecondManifest>(ns, Second);
        var runtime = Runtime(local);

        await runtime.InitializeAsync(ct, debug: true);

        // It can run the stored jobs its build carries, and has no definition for the one only it declares.
        Assert.True(runtime.TryGetDefinitionId(DeployFirstManifest.ChangedJob, out _));
        Assert.False(runtime.TryGetDefinitionId(DeploySecondManifest.AddedJob, out _));
        var after = await ReadDefinitionsAsync(ns, ct);
        Assert.Equal(stored.Keys.Order(), after.Keys.Order());
        Assert.All(after, d => Assert.Equal(stored[d.Key].Version, d.Value.Version));

        await using var nowhere = Build<DeployFirstManifest>(Unique("gate-debug-absent"), First);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Runtime(nowhere).InitializeAsync(ct, debug: true));
    }
}
