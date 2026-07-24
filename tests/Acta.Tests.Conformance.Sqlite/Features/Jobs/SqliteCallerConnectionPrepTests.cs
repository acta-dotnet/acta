using Acta.Payloads;
using Acta.Tests.Conformance.Sqlite.Testing;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.Sqlite;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Sqlite.Features.Jobs;

/// <summary>
/// SQLite-only proof of the narrow caller-connection preparation. Direct transactional enqueue installs
/// the connection-local functions the inline enqueue SQL needs (<c>acta_blob</c>, <c>acta_error</c>) on the
/// caller's own <see cref="SqliteConnection"/> and verifies <c>foreign_keys</c> is enabled, without
/// touching the busy timeout, synchronous mode, or transaction kind. Exercised through the public
/// <c>IJobs.EnqueueAsync(DbTransaction, ...)</c> path on a raw connection Acta did not open, so the
/// StateChange preparation of an Acta-owned connection is not in play. Provider-specific, so it lives in
/// the SQLite head rather than the shared cross-provider contract.
/// </summary>
public sealed class SqliteCallerConnectionPrepTests : ActaRuntimeTestBase<SqliteConformanceFixture, TestJobsManifest>
{
    [Fact(DisplayName = "A caller connection with foreign_keys disabled is rejected before the enqueue runs")]
    public async Task Caller_connection_without_foreign_keys_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;

        // A fresh, unpooled connection with no Foreign Keys keyword defaults foreign_keys OFF.
        await using var conn = new SqliteConnection(RawConnectionString(foreignKeys: false));
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Jobs.EnqueueAsync(tx, Request(), ct));
        Assert.Contains("foreign_keys", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Preparation installs acta_blob and acta_error and leaves busy timeout and synchronous mode untouched")]
    public async Task Preparation_installs_functions_without_touching_pragmas()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var conn = new SqliteConnection(RawConnectionString(foreignKeys: true));
        await conn.OpenAsync(ct);

        var busyBefore = await ScalarAsync(conn, null, "PRAGMA busy_timeout;", ct);
        var syncBefore = await ScalarAsync(conn, null, "PRAGMA synchronous;", ct);

        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        // The enqueue itself relies on acta_blob, so its success proves both functions were installed by
        // the caller-connection preparation on this raw handle.
        var outcome = await Jobs.EnqueueAsync(tx, Request(), ct);
        Assert.Equal(JobEnqueueAction.Inserted, outcome.Action);

        // acta_blob base64-decodes; acta_error raises. Both confirm the functions are present on the handle.
        await using (var blob = conn.CreateCommand())
        {
            blob.Transaction = tx;
            blob.CommandText = "SELECT acta_blob('AQID');";
            Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])(await blob.ExecuteScalarAsync(ct))!);
        }

        // Narrow preparation must not touch these connection-scoped settings.
        Assert.Equal(busyBefore, await ScalarAsync(conn, tx, "PRAGMA busy_timeout;", ct));
        Assert.Equal(syncBefore, await ScalarAsync(conn, tx, "PRAGMA synchronous;", ct));

        await using (var err = conn.CreateCommand())
        {
            err.Transaction = tx;
            err.CommandText = "SELECT acta_error('boom');";
            await Assert.ThrowsAsync<SqliteException>(async () => await err.ExecuteScalarAsync(ct));
        }

        await tx.RollbackAsync(ct);
    }

    private static string RawConnectionString(bool foreignKeys)
    {
        // Set foreign_keys explicitly: the caller must open with it enabled, and the disabled case must be
        // deterministic rather than relying on the provider's default.
        var builder = new SqliteConnectionStringBuilder(SqliteIntegrationSchema.BootstrappedConnectionString)
        {
            Pooling = false,
            ForeignKeys = foreignKeys,
        };
        return builder.ConnectionString;
    }

    private JobEnqueueRequest Request() =>
        new(TestNamespace, "add-numbers", JobPayload.CopyBytes(JobPayloadFormat.Json, "{\"left\":1,\"right\":2}"u8.ToArray()));

    private static async Task<long> ScalarAsync(SqliteConnection conn, SqliteTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}
