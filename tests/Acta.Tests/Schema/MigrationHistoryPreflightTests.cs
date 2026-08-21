using Acta.Relational.Schema;
using Xunit;

namespace Acta.Tests.Schema;

/// <summary>
/// The verdict half of the always-runs migration-history preflight, separated from the database half
/// the conformance specs prove per provider. Everything here is about which histories are acceptable
/// and what the refusal has to say.
/// </summary>
public sealed class MigrationHistoryPreflightTests
{
    private static readonly SchemaMigration[] Shipped = [new(1, "M001_init", ""), new(2, "M002_add_flags", "")];

    private static Dictionary<int, string> History(params (int Version, string Name)[] rows) =>
        rows.ToDictionary(r => r.Version, r => r.Name);

    private static void Verify(Dictionary<int, string> applied) => MigrationHistoryPreflight.Verify(Shipped, applied, "sqlite");

    [Fact]
    public void Complete_history_at_this_baseline_passes()
    {
        Verify(History((0, SchemaMigrationRunner.RequiredBaselineStamp), (1, "init"), (2, "add_flags")));
    }

    [Fact]
    public void Unknown_newer_migrations_pass()
    {
        // An older worker against a newer database is a supported deployment shape, not drift: the
        // preflight requires what this build ships and ignores everything past it.
        Verify(History((0, SchemaMigrationRunner.RequiredBaselineStamp), (1, "init"), (2, "add_flags"), (3, "added_later")));
    }

    [Fact]
    public void Missing_shipped_migration_is_named_with_the_provisioning_script()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Verify(History((0, SchemaMigrationRunner.RequiredBaselineStamp), (1, "init")))
        );

        Assert.Contains("M002_add_flags", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("M001_init", exception.Message, StringComparison.Ordinal);
        Assert.Contains("docs/reference/schema-sqlite.sql", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_baseline_generation_is_refused_before_anything_else()
    {
        // Stamp first: a database on another baseline has migration names that may well match, and
        // "drop and reprovision" is the only useful thing to say about it.
        var exception = Assert.Throws<InvalidOperationException>(() => Verify(History((0, "baseline-0.9"), (1, "init"))));

        Assert.Contains("'baseline-0.9'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("drop and reprovision", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_renamed_on_disk_is_refused()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Verify(History((0, SchemaMigrationRunner.RequiredBaselineStamp), (1, "init"), (2, "add_columns")))
        );

        Assert.Contains("'add_columns'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("drop and reprovision", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_provider_that_joined_late_needs_only_the_migrations_it_ships()
    {
        // A provider added after M002 embeds M003 onward; the leading versions it never shipped are a
        // legal gap in its history, not a missing migration.
        SchemaMigration[] lateJoiner = [new(3, "M003_init", "")];
        MigrationHistoryPreflight.Verify(lateJoiner, History((0, SchemaMigrationRunner.RequiredBaselineStamp), (3, "init")), "sqlite");
    }

    [Fact]
    public void The_unprovisioned_message_names_the_schema_and_the_script()
    {
        var message = MigrationHistoryPreflight.NotProvisioned("acta", "pg").Message;

        Assert.Contains("not provisioned", message, StringComparison.Ordinal);
        Assert.Contains("'acta'", message, StringComparison.Ordinal);
        Assert.Contains("docs/reference/schema-pg.sql", message, StringComparison.Ordinal);
    }
}
