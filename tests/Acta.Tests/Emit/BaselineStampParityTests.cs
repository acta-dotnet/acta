using System.Reflection;
using Acta.Relational.Schema;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Emit;

/// <summary>
/// The baseline stamp is written into the generated M001 bodies by the emitter and required at
/// bootstrap by the runner, from two hand-copied constants in different assemblies. Bumping only one
/// silently defeats the guard that stops a database built from an older baseline taking a mismatched
/// schema, so the two are compared here instead of by a comment.
/// </summary>
public sealed class BaselineStampParityTests
{
    [Fact]
    public void Emitter_and_runner_agree_on_the_baseline_stamp()
    {
        var emitted =
            typeof(Acta.Emit.Shared.Sql.SqlDdlDialect)
                .GetField("BaselineStamp", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                ?.GetRawConstantValue() as string;

        Assert.False(string.IsNullOrEmpty(emitted), "SqlDdlDialect.BaselineStamp was not found; the parity guard is silently passing.");
        Assert.Equal(SchemaMigrationRunner.RequiredBaselineStamp, emitted);
    }

    [Fact]
    public void Every_provider_M001_carries_that_stamp()
    {
        foreach (var provider in (string[])["Acta.SqlServer", "Acta.Postgres", "Acta.Sqlite"])
        {
            var path = Path.Combine(IntegrationConfig.FindRepoRoot(), "src", provider, "Schema", "Migrations", "M001_init.sql");
            Assert.Contains($"'{SchemaMigrationRunner.RequiredBaselineStamp}'", File.ReadAllText(path), StringComparison.Ordinal);
        }
    }
}
