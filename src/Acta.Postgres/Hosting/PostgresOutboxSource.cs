using System.Data.Common;
using Acta.Configuration;
using Acta.Features.Outbox;
using Acta.Postgres.Configuration;
using Acta.Postgres.Services;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Resources;
using Acta.Relational.Stores;

namespace Acta
{
    /// <summary>
    /// Builds the PostgreSQL external-outbox source store: a per-operation source connection, the inline
    /// outbox SQL embedded in this package, and the shared <see cref="RelationalOutboxRelayStore"/>. The
    /// source session is independent of the ledger's <c>IDbSession</c>/<c>ISqlDialect</c>; a PostgreSQL
    /// worker may relay a source on any supported provider.
    /// </summary>
    internal static class PostgresOutboxSource
    {
        public static IOutboxRelayStore CreateStore(string connectionString, string? schema, string table)
        {
            var dialect = new PostgresOutboxDialect();
            // options.Schema drives only routine dispatch (never reached on the inline-only outbox path); the
            // catalog schema drives the actual table qualification and stays null when no override is supplied,
            // so the claim/introspection SQL resolves the table through the connection's search_path.
            var options = new PostgresProviderOptions { ConnectionString = connectionString, Schema = schema ?? "public" };
            var catalog = new SqlResourceCatalog(typeof(PostgresOutboxDialect).Assembly, schema, table);
            return new RelationalOutboxRelayStore(new DbSession(options, dialect, catalog), dialect);
        }
    }

    /// <summary>
    /// Builds the PostgreSQL relay source store from the captured connection string and the source builder's
    /// schema/table overrides. With no schema override the table reference is left unqualified so the
    /// connection's search_path resolves it (matching EF's null-schema mapping); the default table is the
    /// canonical <c>acta_outbox</c>. Registered by <c>source.UsePostgres(...)</c>; resolved lazily, so
    /// construction never opens a connection.
    /// </summary>
    internal sealed class PostgresOutboxSourceStoreFactory(string connectionString) : IOutboxSourceStoreFactory
    {
        public IOutboxRelayStore Create(string? schema, string? table) =>
            PostgresOutboxSource.CreateStore(connectionString, schema, table ?? "acta_outbox");
    }
}

namespace Acta.Postgres.Services
{
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
}
