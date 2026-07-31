using Acta;
#if SMOKE_POSTGRES
using Acta.Postgres;
using Acta.Postgres.Hosting;
using Npgsql;
#elif SMOKE_SQLITE
using Acta.Sqlite;
using Acta.Sqlite.Hosting;
using Microsoft.Data.Sqlite;
#elif SMOKE_SQLSERVER
using Acta.SqlServer;
using Acta.SqlServer.Hosting;
using Microsoft.Data.SqlClient;
#endif

namespace Smoke;

/// <summary>
/// Compile-only proof that the packed provider package ships the raw provider-transaction outbox on-ramp:
/// the canonical DDL source API plus <c>AddToActaOutboxAsync</c> on the concrete provider transaction. The
/// consumer is packed once per provider, so the receiver type and the DDL class switch on a build constant.
/// <see cref="Ddl"/> is pure string building and runs; <see cref="Stage"/> is never invoked (no server), so
/// its being callable against the packed extension is the whole test.
/// </summary>
public static class OutboxProducer
{
    public static string Ddl() =>
#if SMOKE_POSTGRES
        PostgresOutboxDdl.CreateScript();
#elif SMOKE_SQLITE
        SqliteOutboxDdl.CreateScript();
#elif SMOKE_SQLSERVER
        SqlServerOutboxDdl.CreateScript();
#else
        string.Empty;
#endif

    public static Task Stage(
#if SMOKE_POSTGRES
        NpgsqlTransaction transaction,
#elif SMOKE_SQLITE
        SqliteTransaction transaction,
#elif SMOKE_SQLSERVER
        SqlTransaction transaction,
#else
        System.Data.Common.DbTransaction? transaction,
#endif
        CancellationToken ct)
    {
        var request = new JobEnqueueRequest("orders", "send-receipt", JobPayload.Text("o-1"), DeduplicationKey: "o-1");
#if SMOKE_POSTGRES || SMOKE_SQLITE || SMOKE_SQLSERVER
        return transaction.AddToActaOutboxAsync(request, cancellationToken: ct);
#else
        _ = request;
        return Task.CompletedTask;
#endif
    }
}
