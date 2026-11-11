using System.Text.RegularExpressions;
using Acta.Tests.Architecture;
using Xunit;

namespace Acta.Tests.Schema;

/// <summary>
/// An operator view shows a code column's text beside the code. Each such decode must be one code family whole: every
/// id the family has with its text, and nothing else, in every provider's baseline. A decode that misses a member
/// reads NULL for it, and one with a stale text contradicts the API. A payload format decodes its three built-in ids
/// and leaves the rest to its custom arm.
/// </summary>
public sealed partial class OperatorViewDecodeTests
{
    // JobPayloadFormat is no code family: ids past the built-in three are an application's own.
    private const string PayloadFormats = "0=none,1=json,2=bytes";

    [Theory(DisplayName = "Every decode in an operator view is one code family, whole, on every provider")]
    [InlineData("Acta.Postgres")]
    [InlineData("Acta.SqlServer")]
    [InlineData("Acta.Sqlite")]
    public void Every_view_decode_is_one_family_whole(string project)
    {
        var baseline = Assert.Single(
            ShippedCode.SourceFiles("*_init.sql"),
            file => file.Relative.StartsWith($"src/{project}/Migrations/", StringComparison.Ordinal)
        );
        var families = CodeManifests
            .All.GroupBy(entry => entry.CodeKind, StringComparer.Ordinal)
            .Select(family => Canonical(family.Select(entry => ((int)entry.Id, entry.Code))))
            .ToHashSet(StringComparer.Ordinal);

        var sql = File.ReadAllText(baseline.Path);
        var decodes = Decode()
            .Matches(sql)
            .Where(match => InView(sql, match.Index))
            .Select(match =>
                (
                    Label: match.Groups["label"].Value,
                    Format: match.Groups["column"].Value.EndsWith("_format_id", StringComparison.Ordinal),
                    Family: Canonical(
                        Arm().Matches(match.Groups["arms"].Value).Select(arm => (int.Parse(arm.Groups[1].Value), arm.Groups[2].Value))
                    )
                )
            )
            .ToList();

        // The scan must be reading each shape of decode, or a changed shape would pass unseen.
        foreach (var label in (string[])["job_status", "step_status", "job_priority_effective", "alert_profile_effective", "result_format"])
        {
            Assert.Contains(decodes, decode => decode.Label == label);
        }

        Assert.Empty(
            decodes
                .Where(decode => decode.Format ? decode.Family != PayloadFormats : !families.Contains(decode.Family))
                .Select(decode => $"{decode.Label}: {decode.Family}")
        );
    }

    // A routine may hold a CASE of the same shape: only one inside a view's statement is a decode.
    private static bool InView(string sql, int index)
    {
        var statement = sql.LastIndexOf("CREATE ", index, StringComparison.Ordinal);
        return statement >= 0 && ViewHead().IsMatch(sql.AsSpan(statement, Math.Min(40, sql.Length - statement)));
    }

    private static string Canonical(IEnumerable<(int Id, string Code)> members) =>
        string.Join(",", members.OrderBy(member => member.Id).Select(member => $"{member.Id}={member.Code}"));

    // CASE x.some_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'pending' END AS some, on a code column, an effective
    // one, or a payload format, whose ids carry a comment and whose custom ids fall to an ELSE.
    [GeneratedRegex(
        @"CASE\s+(?<column>[\w.]+(?:_code|_code_effective|_format_id))\s+(?<arms>(?:WHEN\s+\d+\s+(?:/\*[^*]*\*/\s+)?THEN\s+N?'[^']*'\s+)+)(?:ELSE\s+[^\n]*\s+)?END\s+AS\s+(?<label>\w+)"
    )]
    private static partial Regex Decode();

    [GeneratedRegex(@"WHEN\s+(\d+)\s+(?:/\*[^*]*\*/\s+)?THEN\s+N?'([^']*)'")]
    private static partial Regex Arm();

    [GeneratedRegex(@"^CREATE\s+(?:OR\s+(?:REPLACE|ALTER)\s+)?VIEW\s")]
    private static partial Regex ViewHead();
}
