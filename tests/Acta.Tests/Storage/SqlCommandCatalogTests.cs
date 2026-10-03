using System.Reflection;
using Acta.Postgres;
using Acta.Runtime;
using Acta.Sqlite;
using Acta.SqlServer;
using Xunit;

namespace Acta.Tests.Storage;

public sealed class SqlCommandCatalogTests
{
    [Fact]
    public void Server_ledgers_have_matching_routines_and_sqlite_uses_inline_commands()
    {
        Assembly[] servers = [typeof(PostgresProviderOptions).Assembly, typeof(SqlServerProviderOptions).Assembly];
        var sqlite = new SqlResourceCatalog(typeof(SqliteProviderOptions).Assembly, "acta");
        Assert.Empty(sqlite.Routines());
        foreach (var assembly in servers)
        {
            var catalog = new SqlResourceCatalog(assembly, "acta");
            var routines = catalog.Routines().ToList();
            Assert.Equal(58, routines.Count);
            foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".routine.sql", StringComparison.Ordinal)))
            {
                var name = resource[..^".routine.sql".Length];
                var command = new StoreCommand(name[(name.LastIndexOf('.') + 1)..]);
                Assert.Equal(StoreExecutionKind.Routine, catalog.Resolve(command));
                Assert.Equal(StoreExecutionKind.Inline, sqlite.Resolve(command));
                Assert.NotEmpty(sqlite.Load(command.Operation));
            }
            Assert.Equal(StoreExecutionKind.Inline, catalog.Resolve(new StoreCommand("ClaimDueRows")));
            Assert.NotEmpty(catalog.Views());
        }
        Assert.Equal(
            new SqlResourceCatalog(servers[0], "acta").Routines().Select(r => r.Name),
            new SqlResourceCatalog(servers[1], "acta").Routines().Select(r => r.Name)
        );
    }

    [Fact]
    public void A_missing_command_fails_without_a_fallback()
    {
        var catalog = new SqlResourceCatalog(typeof(SqliteProviderOptions).Assembly, "acta");
        var error = Assert.Throws<InvalidOperationException>(() => catalog.Resolve(new StoreCommand("Missing")));
        Assert.Contains("embeds no SQL for 'Missing'", error.Message);
    }
}
