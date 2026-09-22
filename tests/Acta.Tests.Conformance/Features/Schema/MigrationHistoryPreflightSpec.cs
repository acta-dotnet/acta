using System.Reflection;
using Acta.Relational.Schema;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Sql;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Features.Schema;

/// <summary>
/// Conformance: provider bootstrap verifies the database's migration history on every start, not only
/// when it is the thing applying migrations.
/// </summary>
[ConformanceSpec(
    "schema.migration-history-preflight",
    "Bootstrap verifies migration history even when it applies nothing",
    Area = "Schema",
    Contract = "Provider bootstrap requires this build's baseline stamp and every migration it ships, and tolerates newer ones.",
    Arrange = "A probe location carries a hand-built migration history, or the shared provisioned schema is used as is.",
    Act = "The provider bootstrap runs against that location with ApplyMigrationsOnStartup left false.",
    Assert = "A complete or newer history passes, while a missing ledger, a foreign baseline, or a missing migration is refused by name."
)]
public abstract class MigrationHistoryPreflightSpec<TFixture> : IntegrationSpec<TFixture>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "A provisioned schema passes bootstrap with ApplyMigrationsOnStartup false")]
    public async Task Provisioned_schema_passes()
    {
        // The shared acta_test schema, exactly as a production host would find its own: provisioned
        // out of band, with this host applying nothing.
        await Fixture.RunBootstrapPreflightAsync(Schema.ConnectionString, Schema.SchemaName, TestContext.Current.CancellationToken);
    }

    [Fact(DisplayName = "An unprovisioned schema is refused as unprovisioned, naming the provisioning script")]
    public async Task Unprovisioned_schema_is_refused()
    {
        await using var probe = await Fixture.CreateHistoryProbeAsync(history: null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(probe));

        Assert.Contains("not provisioned", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"docs/reference/schema-{Fixture.DialectToken}.sql", exception.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A database from another baseline generation is refused with reprovisioning guidance")]
    public async Task Foreign_baseline_is_refused()
    {
        var foreign = ShippedHistory().Select(row => row.Version == 0 ? (0, "baseline-0.9") : row).ToList();
        await using var probe = await Fixture.CreateHistoryProbeAsync(foreign);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(probe));

        Assert.Contains("'baseline-0.9'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("drop and reprovision", exception.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A migration this build ships that the database never applied is refused by version")]
    public async Task Missing_shipped_migration_is_refused()
    {
        var shipped = ShippedHistory();
        var highest = shipped.MaxBy(row => row.Version);
        await using var probe = await Fixture.CreateHistoryProbeAsync(shipped.Where(row => row.Version != highest.Version).ToList());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(probe));

        Assert.Contains($"M{highest.Version:D3}_{highest.Name}", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"docs/reference/schema-{Fixture.DialectToken}.sql", exception.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A history carrying migrations this build has never heard of still passes")]
    public async Task Unknown_newer_migrations_pass()
    {
        // An older worker against a database a newer deploy already migrated. Supported on purpose,
        // so the preflight requires what this build ships and ignores everything past it.
        var shipped = ShippedHistory();
        var ahead = shipped.Append((shipped.Max(row => row.Version) + 1, "applied_by_a_newer_build")).ToList();
        await using var probe = await Fixture.CreateHistoryProbeAsync(ahead);

        await RunAsync(probe);
    }

    [Fact(DisplayName = "A newer object package in the same major starts, and a foreign major or a missing row does not")]
    public async Task Object_package_decides_by_major_and_minimum()
    {
        var ct = TestContext.Current.CancellationToken;
        var shipped = ShippedHistory();

        // The rolling-deploy shape: a newer deploy installed a later package and this worker keeps
        // running against it, because the revision only has to clear the minimum.
        await using (
            var newer = await Fixture.CreateHistoryProbeAsync(WithPackage(shipped, $"objects-{ObjectPackageStamp.ContractMajor}.99-{Hash}"))
        )
        {
            await RunAsync(newer);
        }

        await using (var foreign = await Fixture.CreateHistoryProbeAsync(WithPackage(shipped, $"objects-{ObjectPackageStamp.ContractMajor + 1}.1-{Hash}")))
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(foreign));
            Assert.Contains("not interchangeable in either direction", refused.Message, StringComparison.Ordinal);
        }

        // A database provisioned before the package existed: complete history, no package row.
        await using (var missing = await Fixture.CreateHistoryProbeAsync([.. shipped.Where(row => row.Version != ObjectPackageStamp.HistoryVersion)]))
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(missing));
            Assert.Contains("records no Acta object package", refused.Message, StringComparison.Ordinal);
            Assert.Contains($"docs/reference/schema-{Fixture.DialectToken}.sql", refused.Message, StringComparison.Ordinal);
        }

        Assert.True(ct.CanBeCanceled);
    }

    // The verdict never reads the hash, so any well-formed one serves.
    private const string Hash = "00000000000000000000000000000000";

    private static List<(int Version, string Name)> WithPackage(IReadOnlyList<(int Version, string Name)> shipped, string stamp) =>
        [.. shipped.Select(row => row.Version == ObjectPackageStamp.HistoryVersion ? (row.Version, stamp) : row)];

    private Task RunAsync(IMigrationHistoryProbe probe) =>
        Fixture.RunBootstrapPreflightAsync(probe.ConnectionString, probe.SchemaName, TestContext.Current.CancellationToken);

    /// <summary>
    /// The history a correctly provisioned database of this build would hold: the version-0 baseline
    /// stamp plus one row per embedded migration, keyed by version and carrying the bare snake name
    /// the runner records.
    /// </summary>
    private IReadOnlyList<(int Version, string Name)> ShippedHistory() =>
        [
            (0, BaselineStamps.ForDialect(Fixture.DialectToken)),
            // The object package the install records. These facts are about the history verdicts, and a
            // database missing this row is refused by the fourth for reasons of its own.
            (ObjectPackageStamp.HistoryVersion, ObjectPackageStamp.Format(ObjectPackageHashes.ForDialect(Fixture.DialectToken))),
            .. SchemaMigrationDiscovery
                .Discover(Assembly.Load(ProviderSqlResources.ProviderAssemblyName(Fixture.DialectToken)))
                .Select(migration => (migration.Version, Name: migration.Name[5..])),
        ];
}
