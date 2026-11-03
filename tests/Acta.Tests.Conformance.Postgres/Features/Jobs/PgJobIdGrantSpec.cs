using Acta.Tests.Conformance.Testing;
using Npgsql;
using Xunit;

namespace Acta.Tests.Conformance.Postgres.Features.Jobs;

/// <summary>
/// enqueue_jobs draws a batch's job ids with nextval, which checks the caller's privilege on the sequence. The suite
/// connects as an owner, so nothing else here would notice a runtime login with table privileges only losing it.
/// </summary>
public sealed class PgJobIdGrantSpec : IntegrationSpec<ConformanceFixture>
{
    [Fact(DisplayName = "Any role may draw job ids, so a batch enqueue needs only table privileges")]
    public async Task Public_may_draw_job_ids()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(Schema.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT has_sequence_privilege('public', pg_get_serial_sequence('{Schema.SchemaName}.jobs', 'job_id'), 'USAGE');";

        Assert.Equal(true, await command.ExecuteScalarAsync(ct));
    }
}
