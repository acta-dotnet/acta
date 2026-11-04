using Acta.Tests.Conformance.Testing;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Acta.Tests.Conformance.SqlServer.Features.Schema;

/// <summary>
/// jobs and jobs_state take row locks only: every new job lands on their last pages, and a locking read that scanned them
/// with page locks queued behind every writer there. Losing the option shows elsewhere only as an occasional convoy.
/// </summary>
public sealed class MsSqlRowLocksSpec : IntegrationSpec<ConformanceFixture>
{
    [Fact(DisplayName = "Every index of jobs and jobs_state refuses page locks")]
    public async Task Jobs_and_jobs_state_refuse_page_locks()
    {
        var ct = TestContext.Current.CancellationToken;
        var (indexes, pageLocking) = await TestDdl.RunAsync(async () =>
        {
            await using var connection = new SqlConnection(Schema.ConnectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT COUNT(*), COALESCE(SUM(CASE WHEN allow_page_locks = 1 THEN 1 ELSE 0 END), 0)
                FROM sys.indexes
                WHERE object_id IN (OBJECT_ID(N'{Schema.SchemaName}.jobs'), OBJECT_ID(N'{Schema.SchemaName}.jobs_state'))
                    AND index_id > 0;
                """;
            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            return (reader.GetInt32(0), reader.GetInt32(1));
        });

        Assert.True(indexes > 0, "jobs and jobs_state have no indexes in the shared schema.");
        Assert.Equal(0, pageLocking);
    }
}
