using System.Text.Json;
using Acta.Postgres;
using Acta.Runtime;
using Acta.Sqlite;
using Acta.SqlServer;
using Acta.Tests.Conformance.Sql;
using Xunit;

namespace Acta.Tests.Storage;

public sealed class SqlCommandCatalogTests
{
    [Theory]
    [InlineData("pg")]
    [InlineData("mssql")]
    public void Server_ledgers_install_their_routines_and_sqlite_runs_the_same_commands_inline(string dialect)
    {
        var catalog = new SqlResourceCatalog(dialect, "acta");
        var sqlite = new SqlResourceCatalog("sqlite", "acta");
        var routines = ProviderSqlResources
            .Enumerate(dialect)
            .Where(r => SqlResourceCatalog.DefinesRoutine(r.Sql))
            .Select(r => ProviderSqlResources.Stem(r.LogicalPath))
            .ToList();

        Assert.Equal(37, routines.Count);
        Assert.Equal(
            routines.Select(JsonNamingPolicy.SnakeCaseLower.ConvertName).Order(StringComparer.Ordinal),
            catalog.Routines().Select(r => r.Name)
        );
        foreach (var stem in routines)
        {
            Assert.Equal(StoreExecutionKind.Routine, catalog.Resolve(new StoreCommand(stem)));
            Assert.Equal(StoreExecutionKind.Inline, sqlite.Resolve(new StoreCommand(stem)));
        }
        Assert.Equal(StoreExecutionKind.Inline, catalog.Resolve(new StoreCommand("ClaimOutbox")));
        Assert.NotEmpty(catalog.Views());
        Assert.Empty(sqlite.Routines());
    }

    [Fact]
    public void A_missing_command_fails_without_a_fallback()
    {
        var catalog = new SqlResourceCatalog("sqlite", "acta");
        var error = Assert.Throws<InvalidOperationException>(() => catalog.Resolve(new StoreCommand("Missing")));
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

    [Theory]
    [InlineData("-- leading note\nCREATE OR REPLACE FUNCTION {{schema}}.f() RETURNS void AS $$ $$ LANGUAGE sql;", true)]
    [InlineData("/* a block\n   comment */\r\ncreate or alter procedure {{schema}}.p AS SELECT 1;", true)]
    [InlineData("SELECT 1; -- CREATE OR REPLACE FUNCTION in a trailing comment", false)]
    [InlineData("-- CREATE OR REPLACE FUNCTION only in a comment\nSELECT 1;", false)]
    public void A_routine_is_a_file_whose_first_statement_creates_one(string sql, bool routine) =>
        Assert.Equal(routine, SqlResourceCatalog.DefinesRoutine(sql));
}
