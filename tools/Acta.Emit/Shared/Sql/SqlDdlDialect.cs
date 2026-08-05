using System.Text;
using Acta.Emit.Shared.Model;
using Acta.Relational.Schema;

namespace Acta.Emit.Shared.Sql;

/// <summary>
/// Provider DDL strategy. <see cref="SqlSchemaEmitter"/> walks the model once and delegates every
/// provider-specific syntactic choice to a concrete dialect.
/// </summary>
internal abstract class SqlDdlDialect
{
    public const string SchemaPlaceholder = "{{schema}}";

    // Identity of the current baseline generation, written into the generated M001 bodies and required
    // at bootstrap by SchemaMigrationRunner.RequiredBaselineStamp. The history is not frozen until 1.0,
    // so a re-cut baseline is expected; bump this and that constant together on every `schema reset` so
    // a database built from the previous baseline fails loudly rather than taking a mismatched schema.
    protected const string BaselineStamp = "init-extensible-status-v1";

    protected static string PersistedMigrationName(int version, string name) =>
        version == 1 && string.Equals(name, "init", StringComparison.Ordinal) ? BaselineStamp : name;

    public abstract string ProviderToolName { get; }

    public abstract IReadOnlyList<string> HeaderExtraLines { get; }

    /// <summary>
    /// Whether a sole single-column integer primary key is rendered inline on the column
    /// (<c>INTEGER PRIMARY KEY AUTOINCREMENT</c>, the SQLite rowid alias that auto-assigns ids
    /// without reuse) instead of via a separate table-level constraint with an identity/sequence
    /// default. SQLite returns <c>true</c>.
    /// </summary>
    public virtual bool InlineIntegerPrimaryKey => false;

    /// <summary>
    /// Whether the provider supports covering-index <c>INCLUDE (...)</c> columns. SQL Server and
    /// PostgreSQL return <c>true</c>; SQLite returns <c>false</c> (the include set is dropped).
    /// </summary>
    public virtual bool SupportsIndexInclude => true;

    public abstract string CreateSchemaStatement { get; }

    public abstract string? Terminator { get; }

    /// <summary>
    /// Opening idempotency guard around one table's CREATE TABLE + indexes (SQL Server
    /// <c>IF OBJECT_ID(...) IS NULL BEGIN</c>); null when the dialect guards per statement instead.
    /// </summary>
    public abstract string? TableGuardBegin(string tableName);

    /// <summary>Closing counterpart of <see cref="TableGuardBegin"/>; null when unused.</summary>
    public abstract string? TableGuardEnd { get; }

    /// <summary>The CREATE TABLE keywords, including <c>IF NOT EXISTS</c> where the dialect supports it.</summary>
    public abstract string CreateTableClause { get; }

    /// <summary>The CREATE INDEX keywords, including <c>IF NOT EXISTS</c> where the dialect supports it.</summary>
    public abstract string CreateIndexClause(bool unique);

    /// <summary>
    /// The <c>index-name ON table</c> fragment of a CREATE INDEX. SQL Server / PostgreSQL qualify the
    /// table (<c>idx ON schema.table</c>); SQLite qualifies the index name instead and leaves the
    /// table bare (<c>schema.idx ON table</c>), per its grammar.
    /// </summary>
    public virtual string IndexNameAndTable(string indexName, string tableName) => $"{indexName} ON {SchemaPlaceholder}.{tableName}";

    /// <summary>
    /// The foreign-key target table reference. SQL Server / PostgreSQL qualify it with the schema;
    /// SQLite forbids a schema qualifier in a <c>REFERENCES</c> clause, so it returns the bare table.
    /// </summary>
    public virtual string ForeignKeyTargetTable(string tableName) => $"{SchemaPlaceholder}.{tableName}";

    public abstract string RenderType(ColumnModel c);

    /// <summary>
    /// Renders a store-generated (computed) column definition line from <see cref="ColumnModel.Generated"/>.
    /// Always STORED/PERSISTED and read-only; no NULL/NOT NULL or DEFAULT applies (nullability follows
    /// the expression). SQL Server infers the type (<c>AS (expr) PERSISTED</c>); Postgres requires it
    /// (<c>type GENERATED ALWAYS AS (expr) STORED</c>); SQLite is <c>AS (expr) STORED</c>.
    /// </summary>
    public abstract string RenderGeneratedColumn(ColumnModel c);

    public abstract string RenderDefault(DbDefault d, ColumnModel c);

    public abstract string RenderIdentity();

    /// <summary>
    /// Trailing <c>WITH (...)</c> clause for the table's <c>CREATE TABLE</c>, driven by the entity's
    /// storage flags (e.g. <see cref="DbTableAttribute.PageCompression"/>). Empty when none apply.
    /// </summary>
    public abstract string TableTrailingOptions(EntityModel e);

    /// <summary>
    /// Trailing <c>WITH (...)</c> clause for the inline primary-key constraint, driven by the PK's
    /// storage flags (e.g. <see cref="DbPrimaryKeyAttribute.OptimizeForSequentialKey"/>). Empty when
    /// none apply.
    /// </summary>
    public abstract string PrimaryKeyTrailingOptions(DbPrimaryKeySpec pk);

    /// <summary>
    /// Per-table CHECK constraints the dialect requires beyond the shared set (e.g. Postgres byte
    /// range / bytes octet length, both of which SQL Server enforces natively via tinyint /
    /// varbinary(N)).
    /// </summary>
    public abstract void EmitProviderColumnChecks(StringBuilder sb, EntityModel e);

    /// <summary>
    /// Idempotent INSERT recording this migration in <c>migrations</c> (the table itself is
    /// created by the runner's EnsureMigrations step); <c>applied_at_utc</c> fills from the
    /// table's DB-clock default.
    /// </summary>
    public abstract string MigrationStamp(int version, string name);

    /// <summary>
    /// Provider table-type (TVP) definitions appended verbatim after the entity schema. These are
    /// request-shape table types consumed by hot-path routines (enqueue / claim / schedule batches),
    /// not domain entities, so they live as a dialect literal rather than in the entity model that
    /// drives the data-model docs. Null when the provider needs none (Postgres uses typed arrays).
    /// </summary>
    public virtual string? TrailingTypeDefinitions => null;

    /// <summary>
    /// Bootstrap rows emitted immediately after a given table's DDL (without a trailing terminator:
    /// the emitter appends it). Only the <c>namespaces</c> table returns a value: the reserved system
    /// namespace (id 1, 'sys') that cross-namespace audit events reference. Generated here rather than
    /// hand-appended so <c>schema reset</c> + <c>add</c> reproduces it. Null for every other table.
    /// </summary>
    public virtual string? PostTableSeed(string tableName) => null;
}
