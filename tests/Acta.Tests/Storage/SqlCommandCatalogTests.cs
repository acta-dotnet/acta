using System.Text.Json;
using Acta.Emit.Shared;
using Acta.Emit.Shared.Sql;
using Acta.Tests.Conformance.Sql;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Storage;

public sealed class SqlCommandCatalogTests
{
    // The routines that stay installed: the hot paths, where one round trip and a server-side plan pay for a
    // routine edit costing a migration. Everything else runs inline; adding a routine is a decision, not a drift.
    private static readonly string[] InstalledRoutines =
    [
        "AccessCheckpoint",
        "ArmOrConsumeSleepTimer",
        "ClaimJobs",
        "CompleteExecutions",
        "CompleteStep",
        "EnqueueJob",
        "EnqueueJobs",
        "ExtendWorkerLeases",
        "PurgeExpiredData",
        "RaiseSignal",
        "ReclaimStuckJobs",
        "ReserveRate",
        "StartStep",
        "WaitSignal",
    ];

    [Theory]
    [InlineData("pg")]
    [InlineData("mssql")]
    public void Server_ledgers_install_their_routines_and_sqlite_runs_the_same_commands_inline(string dialect)
    {
        var catalog = new SqlResourceCatalog(dialect, "acta");
        var sqlite = new SqlResourceCatalog("sqlite", "acta");
        var routines = ProviderSqlResources
            .Enumerate(dialect)
            .Where(r => r.LogicalPath.Contains(".routine.", StringComparison.Ordinal))
            .Select(r => ProviderSqlResources.Stem(r.LogicalPath))
            .ToList();

        Assert.Equal(InstalledRoutines, routines.Order(StringComparer.Ordinal));
        // What the migrations install is what the runtime calls as a routine.
        var repoRoot = IntegrationConfig.FindRepoRoot();
        Assert.Equal(
            routines.Select(JsonNamingPolicy.SnakeCaseLower.ConvertName).Order(StringComparer.Ordinal),
            MigrationObjects.For(repoRoot, ProviderCatalog.ByToken(dialect).Dialect).Routines.Select(r => r.Name)
        );
        foreach (var stem in routines)
        {
            Assert.Equal(StoreExecutionKind.Routine, catalog.Resolve(new StoreCommand(stem)));
            Assert.Equal(StoreExecutionKind.Inline, sqlite.Resolve(new StoreCommand(stem)));
        }
        Assert.Equal(StoreExecutionKind.Inline, catalog.Resolve(new StoreCommand("ClaimOutbox")));
        Assert.Empty(MigrationObjects.For(repoRoot, ProviderCatalog.ByToken("sqlite").Dialect).Routines);
    }

    [Fact]
    public void A_missing_command_fails_without_a_fallback()
    {
        var catalog = new SqlResourceCatalog("sqlite", "acta");
        var error = Assert.Throws<InvalidOperationException>(() => catalog.Load("Missing"));
        Assert.Contains("has no file for 'Missing'", error.Message);
    }

    [Fact]
    public void Two_files_with_one_stem_fail_when_the_catalog_is_built()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new SqlResourceCatalog(typeof(SqlCommandCatalogTests).Assembly, "twice", "acta")
        );
        Assert.Contains("two files with the stem 'Twice'", error.Message);
    }
}
