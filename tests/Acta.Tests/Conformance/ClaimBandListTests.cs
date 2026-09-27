using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Sql;
using Xunit;

namespace Acta.Tests.Conformance;

/// <summary>
/// The claim reads one priority band at a time from a list written into each provider's claim script, so
/// a priority code missing from that list is a band no claim ever reads. Each list names every
/// <see cref="JobPriorityCode"/> member exactly once; the SQL code-policy tests hold each literal to its
/// member's value.
/// </summary>
public sealed partial class ClaimBandListTests
{
    [Theory]
    [InlineData("pg", "Sql/Execution/ClaimBatch.routine.sql")]
    [InlineData("mssql", "Sql/Execution/ClaimBatch.routine.sql")]
    [InlineData("sqlite", "Sql/Execution/ClaimBatch.sql")]
    public void Claim_band_list_names_every_priority_code_once(string dialect, string path)
    {
        var sql = ProviderSqlResources.Enumerate(dialect).Single(r => r.LogicalPath == path).Sql;

        var listed = PriorityMember().Matches(sql).Select(m => m.Groups["member"].Value).Order(StringComparer.Ordinal);

        Assert.Equal(Enum.GetNames<JobPriorityCode>().Order(StringComparer.Ordinal), listed);
    }

    [GeneratedRegex(@"/\*\s*JobPriorityCode\.(?<member>\w+)\s*\*/", RegexOptions.CultureInvariant)]
    private static partial Regex PriorityMember();
}
