using System.Reflection;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Sql;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Schema;

/// <summary>
/// Conformance: provider bootstrap verifies the database's migration history on every start, not only
/// when it is the thing applying migrations.
/// </summary>
[ConformanceSpec(
    "schema.migration-history-preflight",
    "Bootstrap verifies migration history even when it applies nothing",
    Area = "Schema",
    Contract = "Provider bootstrap requires every migration this build ships and tolerates newer ones."
)]
public sealed class MigrationHistoryPreflightSpec : IntegrationSpec<ConformanceFixture>
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

    [Fact(DisplayName = "A migration this build ships that the database never applied is refused by name")]
    public async Task Missing_shipped_migration_is_refused()
    {
        var shipped = ShippedHistory();
        var last = shipped[^1];
        await using var probe = await Fixture.CreateHistoryProbeAsync(shipped.SkipLast(1).ToList());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(probe));

        Assert.Contains(last, exception.Message, StringComparison.Ordinal);
        Assert.Contains($"docs/reference/schema-{Fixture.DialectToken}.sql", exception.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A history carrying migrations this build has never heard of still passes")]
    public async Task Unknown_newer_migrations_pass()
    {
        // An older worker against a database a newer deploy already migrated. Supported on purpose,
        // so the preflight requires what this build ships and ignores everything past it.
        var ahead = ShippedHistory().Append("M99991231235959_applied_by_a_newer_build").ToList();
        await using var probe = await Fixture.CreateHistoryProbeAsync(ahead);

        await RunAsync(probe);
    }

    private Task RunAsync(IMigrationHistoryProbe probe) =>
        Fixture.RunBootstrapPreflightAsync(probe.ConnectionString, probe.SchemaName, TestContext.Current.CancellationToken);

    /// <summary>
    /// The history a correctly provisioned database of this build would hold: one row per embedded
    /// migration, in apply order, recorded under its file stem.
    /// </summary>
    private IReadOnlyList<string> ShippedHistory() =>
        [
            .. SchemaMigrations
                .Discover(Assembly.Load(ProviderSqlResources.ProviderAssemblyName(Fixture.DialectToken)))
                .Select(migration => migration.Name),
        ];
}
