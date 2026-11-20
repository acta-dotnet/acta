using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Sql;
using Xunit;

namespace Acta.Tests.Storage;

/// <summary>
/// The manual purge and the retention job delete each name every table that holds a job's rows, so a table added to a
/// job is deleted by both or the two lists drift apart.
/// </summary>
public sealed partial class JobPurgeTablesTests
{
    // A lock's job_id names the job holding it, and the lock goes with its own expiry, so neither purge deletes it.
    private static readonly string[] NotOwned = ["locks"];

    [Theory]
    [InlineData("pg")]
    [InlineData("mssql")]
    [InlineData("sqlite")]
    public void Both_purges_delete_every_table_that_holds_a_jobs_rows(string dialect)
    {
        string[] owned =
        [
            .. ActaSchema
                .Entities.Where(e => e.Columns.Any(c => c.Name == "job_id"))
                .Select(e => e.TableName)
                .Except(NotOwned, StringComparer.Ordinal),
            "tags",
        ];
        Assert.Contains("steps", owned);

        foreach (var stem in new[] { "PurgeJob", "PurgeExpiredJobs" })
        {
            var deleted = DeleteTarget()
                .Matches(SqlCommentPolicyAssert.MaskStringsAndComments(ProviderSqlResources.Read(dialect, stem)))
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);
            Assert.True(
                owned.All(deleted.Contains),
                $"{stem}.{dialect}.sql deletes from none of {string.Join(", ", owned.Except(deleted))}."
            );
        }
    }

    [GeneratedRegex(@"\bDELETE\s+(?:\w+\s+)?FROM\s+\{\{schema\}\}\.(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex DeleteTarget();
}
