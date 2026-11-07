using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Engine.Scheduling;
using Acta.Tests.Conformance.Engine.Worker;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Catalog;

/// <summary>The first release's jobs: one the next release changes, one it leaves alone (with a schedule), one it drops.</summary>
public sealed class DeployFirstManifest : IJobManifest
{
    public const string ChangedJob = "add-numbers";
    public const string ScheduledJob = "recurring-ping";
    public const string DroppedJob = "failures-audit-probe";

    public static JobDescriptorManifest Descriptors { get; } =
        new([.. TestJobsManifest.Descriptors.Descriptors.Where(d => d.JobName is ChangedJob or ScheduledJob or DroppedJob)]);
}

/// <summary>The next release's jobs: <c>add-numbers</c> with one more attempt, <c>recurring-ping</c> as it was, and a new job.</summary>
public sealed class DeploySecondManifest : IJobManifest
{
    public const string AddedJob = RollingGenerationBManifest.NewJob;

    public static JobDescriptorManifest Descriptors { get; } =
        new([
            .. DeployFirstManifest
                .Descriptors.Descriptors.Where(d => d.JobName != DeployFirstManifest.DroppedJob)
                .Select(d => d.JobName == DeployFirstManifest.ChangedJob ? d with { MaxAttempts = d.MaxAttempts + 1 } : d),
            RollingGenerationBManifest.Descriptors.Descriptors.Single(d => d.JobName == AddedJob),
        ]);
}

/// <summary>
/// The <c>jobs deploy</c> and <c>jobs migrate</c> verbs, run through the CLI host a process registers: each run is a
/// release's own container, with its own version and manifest, against the shared schema.
/// </summary>
[ConformanceSpec(
    "catalog.deploy",
    "A deploy writes a release's jobs and records its version",
    Area = "Catalog",
    Contract = "A deploy writes every namespace's definitions, schedules, and version in one transaction, and a version deployed earlier writes nothing unless forced."
)]
public sealed class DeployVerbSpec : ActaStorageTestBase<ConformanceFixture>
{
    private const string First = "1.0.0+b81d04e";
    private const string Second = "1.1.0+5c0ffee";

    private static int FirstAttempts =>
        DeployFirstManifest.Descriptors.Descriptors.Single(d => d.JobName == DeployFirstManifest.ChangedJob).MaxAttempts;

    private Task<(int ExitCode, string Output, string Error)> RunAsync<TManifest>(
        string ns,
        string version,
        CancellationToken ct,
        params string[] args
    )
        where TManifest : class, IJobManifest =>
        RunAsync(j => j.Run<TManifest>(ns, ownerTeam: "billing", description: "Invoices"), version, ct, args);

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(
        Action<IActaBuilder> declare,
        string version,
        CancellationToken ct,
        params string[] args
    )
    {
        var services = new ServiceCollection();
        ActaServiceCollectionExtensions.CliArgsOverride.Value = () => args;
        try
        {
            services.UseActa(j =>
            {
                Fixture.ApplyProvider(j, Schema.SchemaName);
                declare(j);
            });
        }
        finally
        {
            ActaServiceCollectionExtensions.CliArgsOverride.Value = null;
        }

        services.Configure<JobsOptions>(o =>
        {
            o.DeploymentVersion = version;
            o.EnvironmentName = "Staging";
            o.RunSystemJobs = false;
        });
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var host = provider.GetServices<IHostedService>().OfType<CliCommandHost>().Single();
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = await host.RunAsync(output, error, ct);
        return (exitCode, output.ToString(), error.ToString());
    }

    private async Task<NamespaceEntity?> ReadNamespaceAsync(string ns, CancellationToken ct) =>
        await Db.From<NamespaceEntity>().Where(n => n.NamespaceName == ns).SingleOrDefaultAsync(ct);

    private async Task<Dictionary<string, DefinitionEntity>> ReadDefinitionsAsync(string ns, CancellationToken ct)
    {
        var namespaceId = (await ReadNamespaceAsync(ns, ct))!.Id;
        return (await Db.From<DefinitionEntity>().Where(d => d.NamespaceId == namespaceId).ToListAsync(ct)).ToDictionary(d =>
            d.DefinitionName
        );
    }

    private async Task<IReadOnlyList<string>> ReadDeployedVersionsAsync(string ns, CancellationToken ct)
    {
        var namespaceId = (await ReadNamespaceAsync(ns, ct))!.Id;
        return
        [
            .. (await Db.From<DeploymentEntity>().Where(d => d.NamespaceId == namespaceId).ToListAsync(ct))
                .Select(d => d.DeploymentVersion)
                .Order(StringComparer.Ordinal),
        ];
    }

    private async Task<IReadOnlyList<string?>> ReadDeployedEventMessagesAsync(string ns, CancellationToken ct)
    {
        var namespaceId = (await ReadNamespaceAsync(ns, ct))!.Id;
        return
        [
            .. (
                await Db.From<EventEntity>()
                    .Where(e => e.NamespaceId == namespaceId && e.EventCode == EventCode.NamespaceDeployed)
                    .ToListAsync(ct)
            )
                .OrderBy(e => e.Id)
                .Select(e => e.EventMessage),
        ];
    }

    private static string[] Lines(string text) => text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    [Theory(DisplayName = "A deploy writes the definitions and schedules and records the version, and a second run changes nothing")]
    [InlineData("deploy")]
    [InlineData("migrate")]
    public async Task A_deploy_writes_the_catalog_and_records_the_version_and_a_second_run_changes_nothing(string verb)
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Unique("deploy-" + verb);

        var (exitCode, output, error) = await RunAsync<DeployFirstManifest>(ns, First, ct, verb);

        Assert.True(exitCode == 0, error);
        Assert.Contains($"namespace {ns}: {First}", output, StringComparison.Ordinal);
        Assert.Equal(
            [
                $"  added    {DeployFirstManifest.ChangedJob}",
                $"  added    {DeployFirstManifest.DroppedJob}",
                $"  added    {DeployFirstManifest.ScheduledJob}",
            ],
            Lines(output).Where(l => l.StartsWith("  added", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
        );
        var deployed = await ReadNamespaceAsync(ns, ct);
        Assert.Equal((First, "billing", "Invoices"), (deployed!.DeploymentVersion, deployed.OwnerTeam, deployed.Description));
        Assert.Equal([First], await ReadDeployedVersionsAsync(ns, ct));
        var definitions = await ReadDefinitionsAsync(ns, ct);
        Assert.Equal(3, definitions.Count);
        Assert.All(definitions.Values, d => Assert.Equal(DefinitionStatusCode.Active, d.Status));
        await ScheduleTestOps.SlotIdAsync(Db, ns, DeployFirstManifest.ScheduledJob, ct);

        var (againExitCode, againOutput, againError) = await RunAsync<DeployFirstManifest>(ns, First, ct, verb);

        Assert.True(againExitCode == 0, againError);
        Assert.Contains("  3 unchanged", Lines(againOutput));
        Assert.DoesNotContain(Lines(againOutput), l => l.StartsWith("  added", StringComparison.Ordinal));
        var again = await ReadNamespaceAsync(ns, ct);
        Assert.Equal((deployed.Version, deployed.ModifiedAtUtc), (again!.Version, again.ModifiedAtUtc));
        Assert.Equal([First], await ReadDeployedVersionsAsync(ns, ct));
        Assert.All(await ReadDefinitionsAsync(ns, ct), d => Assert.Equal(definitions[d.Key].Version, d.Value.Version));

        // Each run leaves its event, the one that changed nothing too.
        Assert.Equal([$"{First} deployed", $"{First} deployed again"], await ReadDeployedEventMessagesAsync(ns, ct));
    }

    [Fact(DisplayName = "A version never seen deploys over the current one and lists the active definitions it no longer declares")]
    public async Task A_new_version_deploys_over_the_current_one_and_lists_what_it_no_longer_declares()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Unique("deploy-next");
        Assert.Equal(0, (await RunAsync<DeployFirstManifest>(ns, First, ct, "deploy")).ExitCode);

        var (exitCode, output, error) = await RunAsync<DeploySecondManifest>(ns, Second, ct, "deploy");

        Assert.True(exitCode == 0, error);
        Assert.Equal(
            [
                $"namespace {ns}: {Second} (Staging)",
                $"  added    {DeploySecondManifest.AddedJob}",
                $"  changed  {DeployFirstManifest.ChangedJob}",
                "  1 unchanged",
                $"  not declared by this build: {DeployFirstManifest.DroppedJob}",
            ],
            Lines(output)
        );
        Assert.Equal(Second, (await ReadNamespaceAsync(ns, ct))!.DeploymentVersion);
        Assert.Equal([First, Second], await ReadDeployedVersionsAsync(ns, ct));
        var definitions = await ReadDefinitionsAsync(ns, ct);
        Assert.Equal(FirstAttempts + 1, definitions[DeployFirstManifest.ChangedJob].MaxAttempts);
        Assert.Equal(DefinitionStatusCode.Active, definitions[DeployFirstManifest.DroppedJob].Status);
        Assert.Contains(DeploySecondManifest.AddedJob, definitions.Keys);
    }

    [Fact(DisplayName = "A version that deployed earlier and is not current changes nothing and exits zero, and --force deploys it")]
    public async Task An_older_build_changes_nothing_until_it_is_forced()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Unique("deploy-older");
        Assert.Equal(0, (await RunAsync<DeployFirstManifest>(ns, First, ct, "deploy")).ExitCode);
        Assert.Equal(0, (await RunAsync<DeploySecondManifest>(ns, Second, ct, "deploy")).ExitCode);
        var current = await ReadDefinitionsAsync(ns, ct);

        var (exitCode, output, error) = await RunAsync<DeployFirstManifest>(ns, First, ct, "deploy");

        Assert.True(exitCode == 0, error);
        Assert.Equal(
            [
                $"namespace {ns}: deployed at {Second}",
                $"  this build ({First}) deployed earlier; nothing changed",
                "  use --force to deploy it",
            ],
            Lines(output)
        );
        Assert.Equal(Second, (await ReadNamespaceAsync(ns, ct))!.DeploymentVersion);
        Assert.All(await ReadDefinitionsAsync(ns, ct), d => Assert.Equal(current[d.Key].Version, d.Value.Version));
        Assert.Equal([$"{First} deployed", $"{Second} deployed over {First}"], await ReadDeployedEventMessagesAsync(ns, ct));

        var (forcedExitCode, forcedOutput, forcedError) = await RunAsync<DeployFirstManifest>(ns, First, ct, "deploy", "--force");

        Assert.True(forcedExitCode == 0, forcedError);
        Assert.Contains($"  changed  {DeployFirstManifest.ChangedJob}", Lines(forcedOutput));
        Assert.Contains($"  not declared by this build: {DeploySecondManifest.AddedJob}", Lines(forcedOutput));
        Assert.Equal(First, (await ReadNamespaceAsync(ns, ct))!.DeploymentVersion);
        Assert.Equal([First, Second], await ReadDeployedVersionsAsync(ns, ct));
        Assert.Equal(FirstAttempts, (await ReadDefinitionsAsync(ns, ct))[DeployFirstManifest.ChangedJob].MaxAttempts);
        Assert.Equal($"{First} deployed over {Second}", (await ReadDeployedEventMessagesAsync(ns, ct))[^1]);
    }

    [Fact(DisplayName = "A deploy that fails in one namespace leaves every namespace of the host as it was")]
    public async Task A_failure_in_one_namespace_leaves_every_namespace_as_it_was()
    {
        var ct = TestContext.Current.CancellationToken;
        var written = Unique("deploy-a");
        var failing = Unique("deploy-b");
        Assert.Equal(0, (await RunAsync<DeployFirstManifest>(written, First, ct, "deploy")).ExitCode);
        var before = await ReadDefinitionsAsync(written, ct);
        var namespaceBefore = await ReadNamespaceAsync(written, ct);

        // The first namespace in name order is written before the second one's schedules name calendars nobody published.
        var (exitCode, _, error) = await RunAsync(
            j =>
            {
                j.Run<DeploySecondManifest>(written);
                j.Run<CalendarScheduleManifest>(failing);
            },
            Second,
            ct,
            "deploy"
        );

        Assert.Equal(1, exitCode);
        Assert.Contains(CalendarScheduleManifest.CalendarName, error, StringComparison.Ordinal);
        Assert.Null(await ReadNamespaceAsync(failing, ct));

        // The namespace row carries what the schedule registration and the record wrote, and the event is the record's.
        var namespaceAfter = await ReadNamespaceAsync(written, ct);
        Assert.Equal(
            (namespaceBefore!.DeploymentVersion, namespaceBefore.CatalogHash, namespaceBefore.Version, namespaceBefore.ModifiedAtUtc),
            (namespaceAfter!.DeploymentVersion, namespaceAfter.CatalogHash, namespaceAfter.Version, namespaceAfter.ModifiedAtUtc)
        );
        Assert.Equal([First], await ReadDeployedVersionsAsync(written, ct));
        Assert.Equal([$"{First} deployed"], await ReadDeployedEventMessagesAsync(written, ct));
        var after = await ReadDefinitionsAsync(written, ct);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        Assert.All(after, d => Assert.Equal(before[d.Key].Version, d.Value.Version));
    }

    [Fact(
        DisplayName = "A host's namespaces deploy together, each by its own standing: skipped where the build is older, written where it is new"
    )]
    public async Task A_hosts_namespaces_deploy_together_each_by_its_own_standing()
    {
        var ct = TestContext.Current.CancellationToken;
        var ahead = Unique("deploy-ahead");
        var fresh = Unique("deploy-fresh");
        Assert.Equal(0, (await RunAsync<DeployFirstManifest>(ahead, First, ct, "deploy")).ExitCode);
        Assert.Equal(0, (await RunAsync<DeploySecondManifest>(ahead, Second, ct, "deploy")).ExitCode);

        var (exitCode, output, error) = await RunAsync(
            j =>
            {
                j.Run<DeployFirstManifest>(ahead);
                j.Run<DeployFirstManifest>(fresh);
            },
            First,
            ct,
            "deploy"
        );

        Assert.True(exitCode == 0, error);
        Assert.Contains($"namespace {ahead}: deployed at {Second}", Lines(output));
        Assert.Contains($"namespace {fresh}: {First} (Staging)", Lines(output));
        Assert.Equal(Second, (await ReadNamespaceAsync(ahead, ct))!.DeploymentVersion);
        Assert.Equal(First, (await ReadNamespaceAsync(fresh, ct))!.DeploymentVersion);
        Assert.Equal(3, (await ReadDefinitionsAsync(fresh, ct)).Count);

        // A version neither has seen writes both in the one transaction.
        const string third = "1.2.0+0ddba11";
        var (nextExitCode, _, nextError) = await RunAsync(
            j =>
            {
                j.Run<DeploySecondManifest>(ahead);
                j.Run<DeploySecondManifest>(fresh);
            },
            third,
            ct,
            "deploy"
        );

        Assert.True(nextExitCode == 0, nextError);
        Assert.Equal(third, (await ReadNamespaceAsync(ahead, ct))!.DeploymentVersion);
        Assert.Equal(third, (await ReadNamespaceAsync(fresh, ct))!.DeploymentVersion);
        Assert.Equal(FirstAttempts + 1, (await ReadDefinitionsAsync(fresh, ct))[DeployFirstManifest.ChangedJob].MaxAttempts);
        Assert.Equal([$"{First} deployed", $"{third} deployed over {First}"], await ReadDeployedEventMessagesAsync(fresh, ct));
    }

    [Fact(DisplayName = "Two versions deployed at the same moment both land, and the catalog is the one the current version declares")]
    public async Task Two_versions_deployed_together_both_land_and_the_catalog_matches_the_current_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Unique("deploy-race");

        // Each on its own thread, so the two are in flight together on every provider.
        var runs = await Task.WhenAll(
            Task.Run(() => RunAsync<DeployFirstManifest>(ns, First, ct, "deploy"), ct),
            Task.Run(() => RunAsync<DeploySecondManifest>(ns, Second, ct, "deploy"), ct)
        );

        Assert.All(runs, r => Assert.True(r.ExitCode == 0, r.Error));
        Assert.Equal([First, Second], await ReadDeployedVersionsAsync(ns, ct));

        // Whichever committed last deployed whole over the other: its policy stands, and the version says so.
        var currentVersion = (await ReadNamespaceAsync(ns, ct))!.DeploymentVersion;
        var attempts = (await ReadDefinitionsAsync(ns, ct))[DeployFirstManifest.ChangedJob].MaxAttempts;
        Assert.Equal(currentVersion == First ? FirstAttempts : FirstAttempts + 1, attempts);
    }
}
