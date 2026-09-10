using System.Reflection;
using Acta.Postgres.Configuration;
using Acta.Relational.Resources;
using Acta.Sqlite.Configuration;
using Acta.SqlServer.Configuration;
using Xunit;

namespace Acta.Tests.Storage;

public sealed class SqlCommandCatalogTests
{
    [Fact]
    public void Server_ledgers_have_57_matching_routines_and_sqlite_uses_inline_commands()
    {
        Assembly[] servers = [typeof(PostgresProviderOptions).Assembly, typeof(SqlServerProviderOptions).Assembly];
        var sqlite = new SqlResourceCatalog(typeof(SqliteProviderOptions).Assembly, "acta");
        Assert.Empty(sqlite.Routines());
        foreach (var assembly in servers)
        {
            var catalog = new SqlResourceCatalog(assembly, "acta");
            Assert.Equal(57, catalog.Routines().Count());
            var prefix = assembly.GetName().Name + ".Sql.";
            foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".routine.sql", StringComparison.Ordinal)))
            {
                var path = resource[prefix.Length..^".routine.sql".Length].Replace('.', '/');
                var slash = path.IndexOf('/');
                var command = new StoreCommand(path[..slash], path[(slash + 1)..]);
                Assert.Equal(StoreExecutionKind.Routine, catalog.Resolve(command));
                if (path != "Execution/CompleteExecutionsBatch")
                {
                    Assert.Equal(StoreExecutionKind.Inline, sqlite.Resolve(command));
                    Assert.NotEmpty(sqlite.Load(command.SqlPath));
                }
            }
            Assert.Equal(StoreExecutionKind.Inline, catalog.Resolve(new StoreCommand("Outbox", "ClaimDueRows")));
            Assert.NotEmpty(catalog.Views());
        }
        Assert.Equal(
            new SqlResourceCatalog(servers[0], "acta").Routines().Select(r => r.Name),
            new SqlResourceCatalog(servers[1], "acta").Routines().Select(r => r.Name)
        );
    }

    [Theory]
    [InlineData("Ambiguous", "both inline and routine resources exist")]
    [InlineData("Missing", "neither inline nor routine resource exists")]
    public void Ambiguous_or_missing_commands_fail_without_a_fallback(string operation, string message)
    {
        var catalog = new SqlResourceCatalog(typeof(SqlCommandCatalogTests).Assembly, "acta");
        var error = Assert.Throws<InvalidOperationException>(() => catalog.Resolve(new StoreCommand("Testing", operation)));
        Assert.Contains(message, error.Message);
    }
}
