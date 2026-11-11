using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Jobs;

/// <summary>
/// The SQL door refuses a priority that is not a band, by a list in each server routine, so a band added to
/// <see cref="JobPriorityCode"/> has to be added to both lists and to the message a refused caller reads.
/// </summary>
public sealed partial class EnqueuePriorityBandTests
{
    [Theory]
    [InlineData("pg")]
    [InlineData("mssql")]
    public void The_sql_door_lists_every_band(string dialect)
    {
        var routine = Path.Combine("src", "Acta", "Operations", "Jobs", "EnqueueJob", $"EnqueueJob.routine.{dialect}.sql");
        var sql = File.ReadAllText(Path.Combine(IntegrationConfig.FindRepoRoot(), routine));
        int[] bands = [.. Enum.GetValues<JobPriorityCode>().Select(static band => (int)band).Order()];

        var guard = Guard().Match(sql);

        Assert.True(guard.Success, $"{routine} has no priority band guard");
        Assert.Equal(bands, Literal().Matches(guard.Groups["ids"].Value).Select(id => int.Parse(id.Value)).Order());
        Assert.Contains($"is not a priority band ({string.Join(", ", bands)}).", sql, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"p_job_priority_code_override NOT IN \((?<ids>[^()]*)\)")]
    private static partial Regex Guard();

    [GeneratedRegex(@"(?<![\w.])\d+(?=\s*/\*)")]
    private static partial Regex Literal();
}
