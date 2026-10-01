using System.Text.RegularExpressions;
using Acta.Relational.Schema;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Emit;

/// <summary>
/// A published script relocated the way its header says, by replacing the schema name throughout, still
/// takes the installer lock the bootstrap of the new schema takes, so the two keep excluding each other.
/// </summary>
public sealed partial class ProvisionScriptLockKeyTests
{
    [Theory]
    [InlineData("schema-pg.sql")]
    [InlineData("schema-mssql.sql")]
    public void A_relocated_script_takes_the_lock_its_schema_bootstrap_takes(string script)
    {
        var published = File.ReadAllText(Path.Combine(IntegrationConfig.FindRepoRoot(), "docs", "reference", script));

        var relocated = SchemaWord().Replace(published, "billing_ops");

        Assert.Contains($"'{SchemaCommands.LockKey("billing_ops")}'", relocated, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\bacta\b")]
    private static partial Regex SchemaWord();
}
