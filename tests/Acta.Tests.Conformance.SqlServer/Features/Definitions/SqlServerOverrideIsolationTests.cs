using Acta.Tests.Conformance.SqlServer.Testing;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.SqlServer.Features.Definitions;

/// <summary>
/// An overrides update locks its own namespace's definitions only. Its sibling-meter update read the whole of
/// pk_definitions under update locks, so two overrides in two namespaces each held their own row and waited for the
/// other's, and SQL Server chose a deadlock victim.
/// </summary>
public sealed class SqlServerOverrideIsolationTests : ActaRuntimeTestBase<SqlServerConformanceFixture, TestJobsManifest>
{
    // A table of a few definitions, where the optimizer reads pk_definitions whole unless the statement seeks; the shared
    // schema's thousands of namespaces make a seek the cheaper plan anyway and hide the scan.
    protected override bool OwnSchema => true;

    [Fact(DisplayName = "An overrides update leaves another namespace's definition rows free")]
    public async Task Overrides_update_leaves_another_namespaces_rows_free()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeder = new ActaTestSeeder(Db);
        var other = await seeder.SeedJobNamespaceAsync(Unique("other"), ownerTeam: "test", ct: ct);
        var held = await seeder.SeedJobDefinitionAsync(other, Unique("held"), ct: ct);
        await using var holder = new SqlConnection(IntegrationConfig.SqlServerConnectionString);
        await holder.OpenAsync(ct);
        await using var hold = (SqlTransaction)await holder.BeginTransactionAsync(ct);
        // Another namespace's overrides update holds its definition row exclusively until it commits.
        await using (
            var command = new SqlCommand($"UPDATE {Db.Schema}.definitions SET version = version WHERE definition_id = @id", holder, hold)
        )
        {
            command.Parameters.AddWithValue("@id", held);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(ct));
        }

        var definitions = Services.GetRequiredService<IDefinitions>();
        var mine = (await definitions.GetAsync(TestNamespace, "lane-step", ct))!;
        var update = DefinitionTestOps.UpdateOverridesAsync(
            Services,
            TestNamespace,
            "lane-step",
            mine.Version,
            new DefinitionPolicyOverrides(MaxAttempts: 3),
            "test",
            null,
            ct
        );
        // The held row stays locked until the fact ends, so an update that waited on it would never return.
        var first = await Task.WhenAny(update, Task.Delay(SpecWaits.Gate, ct));
        Assert.True(first == update, "the overrides update waited on another namespace's definition row");
        Assert.Equal(ControlAction.Applied, (await update).Action);
    }
}
