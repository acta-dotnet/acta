using System.Data.Common;

namespace Acta.Tests.Conformance.Testing;

public readonly record struct DbIndexInfo(string Name, bool IsUnique, IReadOnlyList<string> Columns);

public readonly record struct DbForeignKeyInfo(string Column, string TargetTable, string TargetColumn, string OnDelete);

public readonly record struct DbCheckInfo(string Name);

/// <summary>
/// Per-provider hook bundle a conformance spec reaches through its base class. The fixture is stateless
/// and constructed via <c>new()</c> by the spec base class.
/// </summary>
/// <remarks>
/// xunit v3 instantiates a fresh test class (and therefore a fresh fixture) per <c>[Fact]</c>, so
/// state on the fixture would leak across parallel-running tests; the instance methods are thin
/// dispatch into static factories that own the schema lifecycle. The shared specs name the fixture
/// through the <c>ConformanceFixture</c> alias each provider test project declares in GlobalUsings.cs.
/// </remarks>
public interface IConformanceFixture
{
    /// <summary>
    /// This provider's dialect token (<c>pg</c>, <c>mssql</c>, <c>sqlite</c>), which selects the
    /// provider assembly whose embedded migrations and SQL resources a provider-neutral spec reads.
    /// </summary>
    string DialectToken { get; }

    /// <summary>
    /// Whether <paramref name="exception"/> is a failure that repeating the statement cures: a deadlock victim, a command
    /// timeout, or a busy database, which the DDL other specs run in parallel can cause.
    /// </summary>
    bool IsTransient(System.Data.Common.DbException exception);

    /// <summary>
    /// The server's cumulative deadlock count, read for the deadlock census; null on SQLite, where one writer forms no
    /// deadlock.
    /// </summary>
    ValueTask<long?> ReadDeadlockCountAsync();

    /// <summary>
    /// Allocate a throwaway location carrying only a migration-history ledger seeded with
    /// <paramref name="history"/> as migration-name rows, or - when <paramref name="history"/>
    /// is <c>null</c> - no ledger at all, standing in for a database that was never provisioned.
    /// </summary>
    ValueTask<IMigrationHistoryProbe> CreateHistoryProbeAsync(IReadOnlyList<string>? history);

    /// <summary>
    /// This provider's <c>SchemaBootstrap</c> over <paramref name="connectionString"/> and <paramref name="schemaName"/>, with
    /// <c>MigrateOnStartup</c> left <c>false</c> as a production host runs it, returned as <see cref="object"/> so the
    /// public fixture surface does not leak the internal type.
    /// </summary>
    object CreateBootstrap(string connectionString, string schemaName);

    /// <summary>An open connection of this provider to <paramref name="connectionString"/>, owned by the caller.</summary>
    Task<DbConnection> OpenConnectionAsync(string connectionString, CancellationToken ct);

    /// <summary>
    /// Apply this build's migrations in <paramref name="schemaName"/> through the provider's public <c>ApplyAsync</c>, as
    /// <c>jobs migrate</c> does, into a database that exists.
    /// </summary>
    Task ApplyMigrationsAsync(string connectionString, string schemaName, CancellationToken ct);

    /// <summary>
    /// Return a handle to the shared <c>acta_test</c> schema, bootstrapping it with the baseline applied on first
    /// touch and caching that bootstrap process-wide; throws via <c>Assert.Skip</c> when the provider env
    /// var is unset. Every spec in the assembly gets a handle to the same schema, not one of its own.
    /// </summary>
    ValueTask<IIntegrationSchema> CreateSchemaAsync();

    /// <summary>
    /// The user-table names present in <paramref name="schemaName"/>, via a provider catalog query.
    /// </summary>
    ValueTask<IReadOnlyList<string>> ListTablesAsync(string schemaName);

    /// <summary>The columns of <paramref name="tableName"/> in <paramref name="schemaName"/> as
    /// (name, nullable) pairs, via a provider catalog query.</summary>
    ValueTask<IReadOnlyList<(string Name, bool Nullable, int? MaxLength)>> ListColumnsAsync(string schemaName, string tableName);

    /// <summary>Indexes on <paramref name="tableName"/> (by name, with uniqueness flag and ordered key columns).</summary>
    ValueTask<IReadOnlyList<DbIndexInfo>> ListIndexesAsync(string schemaName, string tableName);

    /// <summary>Foreign keys on <paramref name="tableName"/> (one entry per column mapping; OnDelete is canonical lowercase).</summary>
    ValueTask<IReadOnlyList<DbForeignKeyInfo>> ListForeignKeysAsync(string schemaName, string tableName);

    /// <summary>Check constraints on <paramref name="tableName"/> with <c>ck_</c>-prefixed names.</summary>
    ValueTask<IReadOnlyList<DbCheckInfo>> ListCheckConstraintsAsync(string schemaName, string tableName);

    /// <summary>
    /// Columns of <paramref name="tableName"/> whose collation deviates from the database default, with that
    /// collation (an explicit COLLATE clause, or a case-insensitive type such as citext).
    /// </summary>
    ValueTask<IReadOnlyList<(string Column, string Collation)>> ListCollationOverridesAsync(string schemaName, string tableName);

    /// <summary>Count user tables in <paramref name="schemaName"/> using a provider-native catalog query.</summary>
    ValueTask<int> CountTablesAsync(string schemaName);

    /// <summary>
    /// Wire the per-provider <c>UseSqlServer</c> / <c>UsePostgres</c> call into
    /// <paramref name="builder"/>, targeting the named <paramref name="schemaName"/>. Used by the
    /// <c>Testing/</c> bases that own DI construction directly (no fixture-built session factory).
    /// </summary>
    void ApplyProvider(Acta.IActaBuilder builder, string schemaName);

    /// <summary>
    /// Ensure a one-column (<c>marker</c>) business probe table named <paramref name="tableName"/> exists
    /// in the Acta schema, using provider-specific idempotent DDL executed on <paramref name="connection"/>
    /// (auto-commit, before the caller's transaction begins), and return the table's provider-qualified
    /// name for the spec's INSERT/SELECT. Lets the transactional-enqueue specs prove that a business insert
    /// and an Acta enqueue commit or roll back on one caller-owned transaction.
    /// </summary>
    ValueTask<string> EnsureBusinessProbeTableAsync(DbConnection connection, string schemaName, string tableName);

    /// <summary>
    /// Create the external-outbox source table named <paramref name="table"/> in the test schema by
    /// executing the provider's <c>{Provider}Migrations.CreateOutboxScript</c> output against the test database
    /// (replacing any prior table of that name), so the DDL API is single-sourced and every relay-store
    /// spec is proof the generated shape works. Per-test table names keep parallel specs from claiming
    /// each other's rows; the DDL derives constraint/index names from the table so they stay unique.
    /// </summary>
    ValueTask ApplyOutboxDdlAsync(string table);

    /// <summary>
    /// Run the provider's public <c>ApplyOutboxAsync</c> for <paramref name="table"/> against the test database: in the test
    /// schema, or with no schema when <paramref name="unqualified"/>, so the connection's default decides. It takes no
    /// <c>TestDdl</c> gate of its own, so a spec that races several calls gates the race as a whole.
    /// </summary>
    Task<bool> ApplyOutboxAsync(string table, bool unqualified = false);

    /// <summary>Drop <paramref name="table"/> when it exists, in the test schema or unqualified; the caller gates it.</summary>
    Task DropOutboxAsync(string table, bool unqualified = false);

    /// <summary>
    /// Open a native provider connection and transaction, insert one business probe row and stage
    /// <paramref name="request"/> through the provider's <c>AddToActaOutboxAsync</c> extension on that
    /// transaction, then commit or roll back the single transaction. Returns the business-row and
    /// outbox-row counts observed afterward on a fresh connection, so a spec can prove the two writes
    /// share one commit boundary.
    /// </summary>
    ValueTask<(int BusinessRows, int OutboxRows)> StageWithBusinessWriteAsync(
        string outboxTable,
        Acta.EnqueueJobRequest request,
        bool commit
    );

    /// <summary>
    /// Stage every request in <paramref name="requests"/>, in order, through the provider's
    /// <c>AddToActaOutboxAsync</c> extension on one native transaction, then commit it.
    /// </summary>
    ValueTask StageInOneTransactionAsync(string outboxTable, IReadOnlyList<Acta.EnqueueJobRequest> requests);

    /// <summary>
    /// Open a native connection and transaction that stages into <paramref name="outboxTable"/> through the
    /// provider's <c>AddToActaOutboxAsync</c> extension and stays open until the spec commits, rolls back, or
    /// disposes it, so a spec can hold one producer open beside another.
    /// </summary>
    ValueTask<OutboxProducer> BeginOutboxProducerAsync(string outboxTable);

    /// <summary>
    /// Build the provider's external-outbox source connection (an internal <c>OutboxSourceConnection</c>, returned
    /// as <see cref="object"/> so the public fixture surface does not leak the internal type) over
    /// <paramref name="table"/>, in the test schema or, when <paramref name="unqualified"/>, with no schema, as a relay
    /// source that names none; the outbox operations open the source over it.
    /// </summary>
    object CreateOutboxConnection(string table, bool unqualified = false);

    /// <summary>Insert one producer row into <paramref name="table"/> with the seeded column values.</summary>
    ValueTask SeedOutboxRowAsync(string table, OutboxSeed seed);

    /// <summary>Read one row's relay-visible state back from <paramref name="table"/>.</summary>
    ValueTask<OutboxRowState> ReadOutboxRowAsync(string table, Guid outboxId);

    /// <summary>Count the rows present in <paramref name="table"/>.</summary>
    ValueTask<int> CountOutboxAsync(string table);

    /// <summary>Rewind every Pending row's next attempt into the past so the next claim finds it due.</summary>
    ValueTask RewindOutboxAsync(string table);

    /// <summary>
    /// Wire this provider's outbox-relay source (<c>source.UseSqlite/UsePostgres/UseSqlServer</c>) at the
    /// fixture's own connection and schema onto <paramref name="source"/>, so a wired <c>sys.outbox</c>
    /// spec can point the relay at a table in the shared test database. The spec sets the source table.
    /// </summary>
    void ApplyOutboxSource(Acta.IOutboxSourceBuilder source);
}
