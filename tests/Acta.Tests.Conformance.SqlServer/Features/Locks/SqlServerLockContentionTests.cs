using System.Data;
using Acta.SqlServer.Configuration;
using Acta.Tests.Conformance.SqlServer.Testing;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Acta.Tests.Conformance.SqlServer.Features.Locks;

public sealed class SqlServerLockContentionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_key_contenders_reach_insert_without_range_locks_and_settle_the_race(bool sameKey)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await ActaSharedDatabase.EnsureReadyAsync(new SqlServerConformanceFixture());
        var schema = "acta_lock_probe_" + Guid.NewGuid().ToString("N");
        var gate = schema + ".insert";
        const string readyMessage = "acta-lock-insert-ready";
        await using var admin = new SqlConnection(database.ConnectionString);
        await admin.OpenAsync(ct);
        await ExecuteAsync(admin, $"CREATE SCHEMA {schema}", ct);
        try
        {
            await ExecuteAsync(
                admin,
                $"""
                SELECT TOP (0) lock_key, job_id, expires_at_utc, hold_token
                INTO {schema}.locks FROM {database.SchemaName}.locks;
                ALTER TABLE {schema}.locks ADD PRIMARY KEY (lock_key);
                CREATE INDEX ix_expiry ON {schema}.locks (expires_at_utc);
                INSERT INTO {schema}.locks VALUES
                    ('a', -1, DATEADD(DAY, 1, SYSUTCDATETIME()), NEWID()),
                    ('z', -1, DATEADD(DAY, 1, SYSUTCDATETIME()), NEWID());
                """,
                ct
            );

            using var stream = typeof(SqlServerProviderOptions).Assembly.GetManifestResourceStream(
                "Acta.SqlServer.Sql.Services.Locks.AcquireLock.routine.sql"
            );
            Assert.NotNull(stream);
            using var text = new StreamReader(stream);
            var body = await text.ReadToEndAsync(ct);
            const string insert = "INSERT INTO {{schema}}.locks";
            Assert.Contains(insert, body, StringComparison.Ordinal);
            // Pause the actual routine after its miss decision. All contenders must reach this point
            // before any INSERT runs. A held gap lock prevents that for distinct keys in the same gap;
            // same-key contenders force the duplicate-key path after the gate opens.
            body = body.Replace(
                    insert,
                    $"""
                    RAISERROR ('{readyMessage}', 10, 1) WITH NOWAIT;
                    DECLARE @gate_result INT;
                    EXEC @gate_result = sys.sp_getapplock
                        @Resource = N'{gate}', @LockMode = 'Shared', @LockOwner = 'Transaction', @LockTimeout = 5000;
                    IF @gate_result < 0 THROW 50000, 'Test insert barrier failed.', 1;
                    {insert}
                    """,
                    StringComparison.Ordinal
                )
                .Replace("{{schema}}", schema, StringComparison.Ordinal);
            body = string.Join('\n', body.Split('\n').Where(line => !line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase)));
            await ExecuteAsync(admin, body, ct);

            await ExecuteAsync(
                admin,
                $"EXEC sys.sp_getapplock @Resource = N'{gate}', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 5000;",
                ct
            );
            const int contenders = 4;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var arrivals = 0;
            var attempts = Enumerable
                .Range(0, contenders)
                .Select(async index =>
                {
                    await using var connection = new SqlConnection(database.ConnectionString);
                    connection.InfoMessage += (_, args) =>
                    {
                        if (
                            args.Message.Contains(readyMessage, StringComparison.Ordinal)
                            && Interlocked.Increment(ref arrivals) == contenders
                        )
                        {
                            ready.TrySetResult();
                        }
                    };
                    await connection.OpenAsync(ct);
                    await using var command = connection.CreateCommand();
                    command.CommandText = schema + ".acquire_lock";
                    command.CommandType = CommandType.StoredProcedure;
                    command.Parameters.Add(
                        new SqlParameter("@p_lock_key", SqlDbType.VarChar, 256) { Value = sameKey ? "m.same" : $"m.{index}" }
                    );
                    command.Parameters.Add(new SqlParameter("@p_job_id", SqlDbType.BigInt) { Value = -1L });
                    command.Parameters.Add(new SqlParameter("@p_lease_ttl_seconds", SqlDbType.Int) { Value = 60 });
                    var token = Guid.NewGuid();
                    command.Parameters.Add(new SqlParameter("@p_hold_token", SqlDbType.UniqueIdentifier) { Value = token });
                    await using var reader = await command.ExecuteReaderAsync(ct);
                    Guid? acquired = null;
                    do
                    {
                        while (await reader.ReadAsync(ct))
                        {
                            Assert.Null(acquired);
                            acquired = reader.GetGuid(0);
                            Assert.Equal(token, acquired.Value);
                        }
                    } while (await reader.NextResultAsync(ct));
                    return acquired;
                })
                .ToArray();
            try
            {
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
            }
            finally
            {
                await ExecuteAsync(
                    admin,
                    $"EXEC sys.sp_releaseapplock @Resource = N'{gate}', @LockOwner = 'Session';",
                    CancellationToken.None
                );
                await Task.WhenAll(attempts);
            }

            var acquired = await Task.WhenAll(attempts);
            Assert.Equal(sameKey ? 1 : contenders, acquired.Count(token => token.HasValue));
            await using var inspect = admin.CreateCommand();
            inspect.CommandText = $"SELECT hold_token FROM {schema}.locks WHERE lock_key LIKE 'm.%'";
            await using var rows = await inspect.ExecuteReaderAsync(ct);
            var stored = new List<Guid>();
            while (await rows.ReadAsync(ct))
            {
                stored.Add(rows.GetGuid(0));
            }
            Assert.Equal(acquired.Where(token => token.HasValue).Select(token => token!.Value).Order(), stored.Order());
        }
        finally
        {
            await ExecuteAsync(
                admin,
                $"DROP PROCEDURE IF EXISTS {schema}.acquire_lock; DROP TABLE IF EXISTS {schema}.locks; DROP SCHEMA {schema};",
                CancellationToken.None
            );
        }
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}
