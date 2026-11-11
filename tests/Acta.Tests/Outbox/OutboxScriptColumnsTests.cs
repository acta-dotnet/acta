using Acta.Postgres;
using Acta.Sqlite;
using Acta.SqlServer;
using Xunit;

namespace Acta.Tests.Outbox;

/// <summary>
/// The outbox table is the one table an application creates and writes its own SQL against, so its column names are a
/// contract: one list, in one order, on every provider.
/// </summary>
public sealed class OutboxScriptColumnsTests
{
    private static readonly string[] Columns =
    [
        "id",
        "outbox_id",
        "namespace_name",
        "job_name",
        "input_format_id",
        "input",
        "deduplication_key",
        "correlation_key",
        "concurrency_key",
        "lane_name",
        "job_priority_code",
        "next_run_at_utc",
        "delay_seconds",
        "tenant_name",
        "meta",
        "failure_message",
        "created_at_utc",
        "outbox_status_code",
        "failure_count",
        "next_attempt_at_utc",
        "claim_token",
        "claim_until_utc",
    ];

    [Theory(DisplayName = "The outbox table has the same columns, in the same order, on every provider")]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    [InlineData("sqlite")]
    public void The_table_has_its_columns_in_order(string provider)
    {
        var script = provider switch
        {
            "postgres" => PostgresMigrations.CreateOutboxScript(),
            "sqlserver" => SqlServerMigrations.CreateOutboxScript(),
            _ => SqliteMigrations.CreateOutboxScript(),
        };

        Assert.Equal(Columns, ColumnsOf(script));
    }

    // A column is a line of the CREATE TABLE, at the columns' own indent, that does not open a constraint: a constraint's
    // later lines sit deeper, so a column written after one is still read.
    private static string[] ColumnsOf(string script) =>
        [
            .. script
                .Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .SkipWhile(line => !line.StartsWith("CREATE TABLE", StringComparison.Ordinal))
                .Skip(1)
                .TakeWhile(line => !line.StartsWith(')'))
                .Where(line => line.StartsWith("    ", StringComparison.Ordinal) && line[4] != ' ')
                .Select(line => line.Trim())
                .Where(line => !line.StartsWith("CONSTRAINT", StringComparison.Ordinal))
                .Select(line => line[..line.IndexOf(' ', StringComparison.Ordinal)]),
        ];
}
