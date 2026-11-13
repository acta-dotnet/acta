using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Scheduling;

/// <summary>
/// The admitter's two reads list the schedule codes this build can evaluate, so a member added to one of those
/// families has to be added to both reads, in every dialect, or the build that introduced it never fires its schedules.
/// </summary>
public sealed partial class ScheduleCodeListTests
{
    private static readonly string[] Reads =
    [
        "Operations/Schedules/ListDueSchedules/ListDueSchedules",
        "Operations/Schedules/GetScheduleHorizon/GetScheduleHorizon",
    ];

    public static TheoryData<string, string> Lists
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var read in Reads)
            {
                foreach (var dialect in new[] { "pg", "mssql", "sqlite" })
                {
                    foreach (var column in Expected.Keys)
                    {
                        data.Add($"{read}.{dialect}.sql", column);
                    }
                }
            }

            return data;
        }
    }

    // An orphaned schedule is this build's own and is left out on purpose; id 0 is what an unknown id decodes to.
    private static readonly Dictionary<string, int[]> Expected = new()
    {
        ["schedule_status_code"] = Ids<ScheduleStatusCode>(ScheduleStatusCode.Orphaned),
        ["expression_kind_code"] = Ids<ScheduleExpressionKindCode>(),
        ["misfire_strategy_code"] = Ids<MisfireStrategyCode>(),
        ["full_behavior_code"] = Ids<ScheduleFullBehaviorCode>(),
        ["expire_action_code"] = Ids<ScheduleExpireActionCode>(),
    };

    [Theory]
    [MemberData(nameof(Lists))]
    public void Every_read_lists_every_code_this_build_knows(string file, string column)
    {
        var sql = File.ReadAllText(Path.Combine(IntegrationConfig.FindRepoRoot(), "src", "Acta", file));

        var lists = CodeList().Matches(sql).Where(m => m.Groups["column"].Value == column).ToList();

        Assert.NotEmpty(lists);
        Assert.All(lists, list =>
        {
            int[] listed = [.. Literal().Matches(list.Groups["ids"].Value).Select(id => int.Parse(id.Value)).Order()];
            Assert.Equal(Expected[column], listed);
        });
    }

    private static int[] Ids<T>(params T[] leftOut)
        where T : struct, Enum =>
        [.. Enum.GetValues<T>().Except(leftOut).Select(static v => Convert.ToInt32(v)).Where(static id => id != 0).Order()];

    [GeneratedRegex(@"\w+\.(?<column>\w+_code) IN \((?<ids>[^()]*)\)", RegexOptions.Singleline)]
    private static partial Regex CodeList();

    [GeneratedRegex(@"(?<![\w.])\d+(?=\s*/\*)")]
    private static partial Regex Literal();
}
