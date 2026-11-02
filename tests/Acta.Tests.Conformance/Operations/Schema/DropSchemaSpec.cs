using System.Data.Common;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schema;

/// <summary>
/// Conformance for <c>DropSchema</c>: the drop half of a schema reset, which must leave nothing of Acta's for the apply
/// half to trip over. SQLite drops by name, so this is also what catches a table its drop list lacks.
/// </summary>
[ConformanceSpec(
    "drop-schema.removes-objects",
    "Dropping the schema removes every table Acta installed",
    Area = "Schema",
    Contract = "DropSchema removes every modelled table and the migration-history table, and dropping a schema that is already gone succeeds."
)]
public sealed class DropSchemaSpec : IntegrationSpec<ConformanceFixture>
{
    private const int TimeoutSeconds = 30;

    [Fact(DisplayName = "An installed schema loses every table and its history, and a second drop succeeds")]
    public async Task Drops_every_installed_object()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var probe = await Fixture.CreateHistoryProbeAsync(history: null);
        await SpecWaits.RetryTransientAsync(Fixture, () => Fixture.ApplyMigrationsAsync(probe.ConnectionString, probe.SchemaName, ct), ct);
        var sql = new SqlResourceCatalog(Fixture.DialectToken, probe.SchemaName);
        string[] objects = ["migrations", .. ActaSchema.Entities.Select(e => e.TableName)];
        await using var connection = await Fixture.OpenConnectionAsync(probe.ConnectionString, ct);
        var drop = new DropSchemaHandler(connection, sql, TimeoutSeconds);
        Assert.Equal(objects, await InstalledAsync(connection, probe.SchemaName, objects, ct));

        await SpecWaits.RetryTransientAsync(Fixture, () => drop.HandleAsync(ct), ct);

        var left = await InstalledAsync(connection, probe.SchemaName, objects, ct);
        Assert.True(left.Count == 0, $"DropSchema left {string.Join(", ", left)} behind; on SQLite, name each in DropSchema.sqlite.sql.");
        await SpecWaits.RetryTransientAsync(Fixture, () => drop.HandleAsync(ct), ct);
    }

    /// <summary>The objects the catalog lists in the schema, in the order of <paramref name="objects"/>.</summary>
    private async Task<List<string>> InstalledAsync(DbConnection connection, string schema, string[] objects, CancellationToken ct)
    {
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await SpecWaits.RetryTransientAsync(
            Fixture,
            async () =>
            {
                listed.Clear();
                await using var list = connection.CreateCommand();
                if (Fixture.DialectToken == "sqlite")
                {
                    list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
                }
                else
                {
                    list.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema = @schema";
                    var parameter = list.CreateParameter();
                    parameter.ParameterName = "@schema";
                    parameter.Value = schema;
                    list.Parameters.Add(parameter);
                }
                await using var reader = await list.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    listed.Add(reader.GetString(0));
                }
            },
            ct
        );
        return [.. objects.Where(listed.Contains)];
    }
}
