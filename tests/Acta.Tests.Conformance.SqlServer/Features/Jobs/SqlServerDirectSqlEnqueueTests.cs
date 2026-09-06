using Acta.Runtime.Modules.Execution;
using Acta.Tests.Conformance.SqlServer.Testing;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.SqlServer.Features.Jobs;

public sealed class SqlServerDirectSqlEnqueueTests : ActaRuntimeTestBase<SqlServerConformanceFixture, TestJobsManifest>
{
    [Fact(DisplayName = "Documented direct SQL enqueue defaults produce a visible and executable job")]
    public async Task Sql_enqueue_is_visible_and_claimable_without_an_application_wake()
    {
        var ct = TestContext.Current.CancellationToken;
        long jobId;
        Guid jobRef;
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(Schema.ConnectionString);
        await connection.OpenAsync(ct);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                DECLARE @tags {Schema.SchemaName}.job_enqueue_tag_batch;
                EXEC {Schema.SchemaName}.enqueue_one
                    @p_namespace_name = @namespace,
                    @p_job_name = 'add-numbers',
                    @p_input = 0x7B226C656674223A312C227269676874223A327D,
                    @p_tag_batch = @tags;
                """;
            command.Parameters.Add(
                new Microsoft.Data.SqlClient.SqlParameter("@namespace", System.Data.SqlDbType.VarChar, 128) { Value = TestNamespace }
            );
            await using var reader = await command.ExecuteReaderAsync(ct);
            Assert.True(await reader.ReadAsync(ct));
            Assert.Equal(0, reader.GetInt32(0));
            jobId = reader.GetInt64(1);
            jobRef = reader.GetGuid(2);
            Assert.Equal(1, Convert.ToInt32(reader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture));
            Assert.False(await reader.ReadAsync(ct));
            while (await reader.NextResultAsync(ct)) { }
        }
        await using (var inspect = connection.CreateCommand())
        {
            inspect.CommandText = $"SELECT job_ref FROM {Schema.SchemaName}.jobs_view WHERE job_id = @id";
            inspect.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@id", System.Data.SqlDbType.BigInt) { Value = jobId });
            Assert.Equal(jobRef, (Guid)(await inspect.ExecuteScalarAsync(ct))!);
        }
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, jobId, ct));
    }
}
