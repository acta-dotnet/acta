using Acta.Sqlite;
using Xunit;

namespace Acta.Tests.Schema;

/// <summary>
/// The verdict half of the always-runs migration-history preflight, separated from the database half
/// the conformance specs prove per provider. Everything here is about which histories are acceptable
/// and what the refusal has to say.
/// </summary>
public sealed class MigrationHistoryPreflightTests
{
    private static readonly SchemaMigration[] Shipped = [new("M20260101000000_init", ""), new("M20260102000000_add_flags", "")];

    private static void Verify(params string[] applied) =>
        SchemaMigrations.Verify(Shipped, applied.ToHashSet(StringComparer.Ordinal), "sqlite");

    [Fact]
    public void Complete_history_passes()
    {
        Verify("M20260101000000_init", "M20260102000000_add_flags");
    }

    [Fact]
    public void Unknown_newer_migrations_pass()
    {
        // An older worker against a newer database is a supported deployment shape, not drift: the
        // preflight requires what this build ships and ignores everything past it.
        Verify("M20260101000000_init", "M20260102000000_add_flags", "M20260103000000_added_later");
    }

    [Fact]
    public void A_newer_migration_whose_name_ends_in_init_is_not_a_baseline()
    {
        Verify("M20260101000000_init", "M20260102000000_add_flags", "M20260103000000_cache_init");
    }

    [Fact]
    public void A_baseline_this_build_does_not_ship_is_refused_by_name()
    {
        // An earlier release candidate's cut: applying this build's baseline over it would change nothing.
        var exception = Assert.Throws<InvalidOperationException>(() => Verify("M20251201000000_init", "M20260101000000_init"));

        Assert.Contains("M20251201000000_init", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ships M20260101000000_init", exception.Message, StringComparison.Ordinal);
        Assert.Contains("docs/reference/schema-sqlite.sql", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_shipped_migration_is_named_with_the_provisioning_script()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Verify("M20260101000000_init"));

        Assert.Contains("M20260102000000_add_flags", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("M20260101000000_init", exception.Message, StringComparison.Ordinal);
        Assert.Contains("docs/reference/schema-sqlite.sql", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_unprovisioned_message_names_the_schema_and_the_script()
    {
        var message = SchemaMigrations.NotProvisioned("acta", "pg").Message;

        Assert.Contains("not provisioned", message, StringComparison.Ordinal);
        Assert.Contains("'acta'", message, StringComparison.Ordinal);
        Assert.Contains("docs/reference/schema-pg.sql", message, StringComparison.Ordinal);
        Assert.Contains("cannot read it", message, StringComparison.Ordinal);
    }
}

/// <summary>
/// SQLite alone can be asked about a database that does not exist, because opening a connection is
/// what would create it. A read-only preflight must answer without provisioning anything.
/// </summary>
public sealed class SqlitePreflightFileSourceTests
{
    [Fact]
    public async Task A_missing_database_file_is_refused_without_being_created()
    {
        var path = Path.Combine(Path.GetTempPath(), $"acta-preflight-absent-{Guid.NewGuid():N}.db");
        Assert.False(File.Exists(path));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqliteSchemaMigrator.PreflightAsync($"Data Source={path}", "main", TestContext.Current.CancellationToken)
        );

        Assert.Contains("not provisioned", exception.Message, StringComparison.Ordinal);
        Assert.Contains("docs/reference/schema-sqlite.sql", exception.Message, StringComparison.Ordinal);
        // The point of the case: a typo'd Data Source must not leave a stray empty database behind on
        // the way to reporting that none is there.
        Assert.False(File.Exists(path), "the read-only preflight created the database file it was reporting as absent.");
    }

    [Fact]
    public async Task A_memory_source_still_opens_and_reports_the_missing_ledger_with_the_provider_message()
    {
        // There is no file to miss, so the pre-check must not claim one: the verdict has to come from
        // actually reading the opened database, and the provider's own error says what stopped the read.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqliteSchemaMigrator.PreflightAsync("Data Source=:memory:", "main", TestContext.Current.CancellationToken)
        );

        Assert.Contains("not provisioned", exception.Message, StringComparison.Ordinal);
        Assert.Contains("no such table", exception.Message, StringComparison.Ordinal);
    }
}
