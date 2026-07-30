using System.Data.Common;
using Acta.Configuration;
using Acta.Modules.Outbox;
using Acta.Postgres.Configuration;
using Acta.Postgres.Services;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Resources;
using Acta.Relational.Stores;

namespace Acta.Postgres.Services;

/// <summary>
/// PostgreSQL external-outbox source dialect. Inline claim/finalize SQL only; connection creation,
/// parameter binding, and deadlock classification reuse the ledger <see cref="PostgresDialect"/> because
/// the canonical outbox column types (timestamptz, uuid, bytea, smallint) coincide with the ledger's.
/// </summary>
internal sealed class PostgresOutboxDialect : OutboxSourceDialect
{
    private readonly PostgresDialect _inner = new();

    public override DbProvider Provider => DbProvider.Postgres;

    public override string DialectToken => "pg";

    public override DbConnection CreateConnection(string connectionString) => _inner.CreateConnection(connectionString);

    public override bool OwnsConnection(DbConnection connection) => _inner.OwnsConnection(connection);

    public override bool IsTransientConflict(Exception exception) => _inner.IsTransientConflict(exception);

    public override DbParameter CreateParameter(DbParameterSpec spec) => _inner.CreateParameter(spec);
}
