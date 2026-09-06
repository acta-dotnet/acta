using Acta.Runtime.Modules.Execution;
using Acta.Tests.Conformance.Postgres.Testing;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Postgres.Features.Jobs;

public sealed class PgDirectSqlEnqueueTests : ActaRuntimeTestBase<PgConformanceFixture, TestJobsManifest>
{
    [Fact(DisplayName = "Documented direct SQL enqueue defaults produce a visible and executable job")]
    public async Task Sql_enqueue_is_visible_and_claimable_without_an_application_wake()
    {
        var ct = TestContext.Current.CancellationToken;
        long jobId;
        Guid jobRef;
        await using var connection = new Npgsql.NpgsqlConnection(Schema.ConnectionString);
        await connection.OpenAsync(ct);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $$"""
                SELECT ordinal, job_id, job_ref, action
                FROM {{Schema.SchemaName}}.enqueue_one(
                    p_namespace_name => @namespace,
                    p_job_name => 'add-numbers',
                    p_input => convert_to('{"left":1,"right":2}', 'UTF8'));
                """;
            command.Parameters.Add(new Npgsql.NpgsqlParameter("namespace", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = TestNamespace });
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
            inspect.Parameters.Add(new Npgsql.NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = jobId });
            Assert.Equal(jobRef, (Guid)(await inspect.ExecuteScalarAsync(ct))!);
        }
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, jobId, ct));
    }
}
