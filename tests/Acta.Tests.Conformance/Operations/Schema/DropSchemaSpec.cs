using System.Data.Common;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schema;

/// <summary>
/// Conformance for <c>DropSchema</c>: the drop half of a schema reset, which must leave nothing of Acta's for the apply
/// half to trip over. SQLite drops by name, so this is also what catches a table or view its drop list lacks.
/// </summary>
[ConformanceSpec(
    "drop-schema.removes-objects",
    "Dropping the schema removes every table and view Acta installed",
    Area = "Schema",
    Contract = "DropSchema removes every modelled table, every operator view, and the migration-history table, and dropping a schema that is already gone succeeds."
)]
public sealed class DropSchemaSpec : IntegrationSpec<ConformanceFixture>
{
    private const int TimeoutSeconds = 30;

    [Fact(DisplayName = "An installed schema loses every table, view, and its history, and a second drop succeeds")]
    public async Task Drops_every_installed_object()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var probe = await Fixture.CreateHistoryProbeAsync(history: null);
        await Fixture.ApplyMigrationsAsync(probe.ConnectionString, probe.SchemaName, ct);
        var sql = new SqlResourceCatalog(Fixture.DialectToken, probe.SchemaName);
        string[] objects = ["migrations", .. ActaSchema.Entities.Select(e => e.TableName), .. sql.Views().Select(v => v.Name)];
        await using var connection = await Fixture.OpenConnectionAsync(probe.ConnectionString, ct);
        var drop = new DropSchemaHandler(connection, sql, TimeoutSeconds);
        Assert.Equal(objects, await ReadableAsync(connection, probe.SchemaName, objects, ct));

        await drop.HandleAsync(ct);

        var left = await ReadableAsync(connection, probe.SchemaName, objects, ct);
        Assert.True(left.Count == 0, $"DropSchema left {string.Join(", ", left)} behind; on SQLite, name each in DropSchema.sqlite.sql.");
        await drop.HandleAsync(ct);
    }

    // The objects a zero-row select can read; one that is gone fails the select.
    private static async Task<List<string>> ReadableAsync(DbConnection connection, string schema, string[] objects, CancellationToken ct)
    {
        var readable = new List<string>();
        foreach (var name in objects)
        {
            await using var select = connection.CreateCommand();
            select.CommandText = $"SELECT 1 FROM {schema}.{name} WHERE 1 = 0";
            try
            {
                await select.ExecuteScalarAsync(ct);
                readable.Add(name);
            }
            catch (DbException) { }
        }
        return readable;
    }
}
