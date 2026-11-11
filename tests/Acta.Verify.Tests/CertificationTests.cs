using System.Text.RegularExpressions;
using Acta.Verify;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Acta.Verify.Tests;

/// <summary>
/// certify.sql held to Acta: negative controls that plant the breach a check exists to catch and expect the check to
/// name it, and its code literals checked against the enums.
/// </summary>
public sealed class CertificationTests
{
    [Fact]
    public void Namespace_isolation_names_an_attempt_that_ran_on_another_namespaces_worker()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        // Only the columns the check reads. Job 101 ran on worker 202 of namespace 2; job 102 on its own namespace's worker.
        Execute(
            db,
            """
            CREATE TABLE jobs (job_id INTEGER, namespace_id INTEGER);
            CREATE TABLE workers (worker_id INTEGER, namespace_id INTEGER);
            CREATE TABLE events (entity_code INTEGER, entity_id INTEGER, event_code INTEGER, namespace_id INTEGER, worker_id INTEGER);
            INSERT INTO jobs VALUES (101, 1), (102, 1);
            INSERT INTO workers VALUES (201, 1), (202, 2);
            INSERT INTO events VALUES (50, 101, 40, 1, 202), (50, 102, 40, 1, 201);
            """
        );

        using var command = db.CreateCommand();
        command.CommandText = Check("namespace-isolation");
        using var reader = command.ExecuteReader();
        List<long> jobs = [];
        while (reader.Read())
        {
            jobs.Add(reader.GetInt64(reader.GetOrdinal("job_id")));
        }

        Assert.Equal([101L], jobs);
    }

    [Fact]
    public void The_runner_parses_every_check()
    {
        // The runner reads certify.sql once, in its type initializer, and a statement it cannot place throws there.
        var runner = typeof(FixtureValueV1).Assembly.GetType("Acta.Verify.LedgerValidation", throwOnError: true)!;
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(runner.TypeHandle);
    }

    [Fact]
    public void Every_code_literal_names_its_member_and_holds_its_value()
    {
        var sql = CertifySql();
        Assert.Empty(Regex.Matches(sql, @"[a-z_]+_code\s*(?:=|<>|(?:NOT\s+)?IN\s*\(|BETWEEN)\s*\d+(?!\d)(?!\s*/\*)").Select(m => m.Value));

        var named = Regex.Matches(sql, @"(\d+) /\* ([A-Za-z]+)\.([A-Za-z]+) \*/");
        Assert.NotEmpty(named);
        foreach (Match m in named)
        {
            var type = typeof(JobStatusCode).Assembly.GetType("Acta." + m.Groups[2].Value, throwOnError: true)!;
            var value = Convert.ToInt64(Enum.Parse(type, m.Groups[3].Value), System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(
                value == long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                $"{m.Value}: the member is {value}"
            );
        }
    }

    private static string CertifySql()
    {
        using var stream = typeof(FixtureValueV1).Assembly.GetManifestResourceStream("Acta.Verify.Certification.certify.sql")!;
        using var file = new StreamReader(stream);
        return file.ReadToEnd().ReplaceLineEndings("\n");
    }

    /// <summary>The certify.sql statement named <paramref name="name"/>, its comments dropped and its schema prefix empty.</summary>
    private static string Check(string name)
    {
        var statement = CertifySql().Split(";\n").Single(chunk => chunk.Contains($"'{name}' AS check_name", StringComparison.Ordinal));
        return string.Join('\n', statement.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)))
            .Replace("{s}", "", StringComparison.Ordinal);
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
