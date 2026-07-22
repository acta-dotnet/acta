using System.Globalization;
using System.Text;
using Acta.Emit.Shared.Model;
using Acta.Relational.Schema;

namespace Acta.Emit.Shared.Sql;

/// <summary>
/// SQLite DDL strategy. SQLite is embedded and single-node: no schema container, no stored routines,
/// no sequences, no covering-index INCLUDE, and dynamic typing. Integer surrogate keys are rendered
/// as the rowid alias with <c>AUTOINCREMENT</c>; instants are INTEGER epoch-milliseconds so they
/// compare chronologically; payloads are BLOB. Provider feature resources own executable inline SQL.
/// </summary>
internal sealed class SqliteDdlDialect : SqlDdlDialect
{
    public override string ProviderToolName => "sqlite";

    public override IReadOnlyList<string> HeaderExtraLines =>
        [
            "-- SQLite: no schema container ({{schema}} is the attached database, normally `main`);",
            "--         DbKind.UtcInstant -> INTEGER (epoch milliseconds); DbKind.Byte/Short/Int/Long -> integer;",
            "--         DbKind.BinaryPayload/Bytes -> BLOB; surrogate keys use AUTOINCREMENT rowids.",
        ];

    public override bool InlineIntegerPrimaryKey => true;

    public override bool SupportsIndexInclude => false;

    // SQLite has no CREATE SCHEMA; everything lives in the attached database. The emitted statement is
    // an empty placeholder so the migration script stays structurally uniform across providers.
    public override string CreateSchemaStatement =>
        "-- SQLite has no schema container; tables are created in the attached database ({{schema}}).";

    public override string? Terminator => null;

    public override string? TableGuardBegin(string tableName) => null;

    public override string? TableGuardEnd => null;

    public override string CreateTableClause => "CREATE TABLE IF NOT EXISTS";

    public override string CreateIndexClause(bool unique) => unique ? "CREATE UNIQUE INDEX IF NOT EXISTS" : "CREATE INDEX IF NOT EXISTS";

    // SQLite puts the schema qualifier on the index name; the table is referenced bare (same schema).
    public override string IndexNameAndTable(string indexName, string tableName) => $"{SchemaPlaceholder}.{indexName} ON {tableName}";

    // SQLite forbids a schema qualifier in a REFERENCES clause; the target is always the bare table.
    public override string ForeignKeyTargetTable(string tableName) => tableName;

    // STRICT tables require a datatype on every column, generated ones included; STORED materializes it.
    public override string RenderGeneratedColumn(ColumnModel c) => $"{c.Name} {RenderType(c)} AS ({c.Generated}) STORED";

    public override string RenderType(ColumnModel c) =>
        c.Kind switch
        {
            DbKind.Boolean => "integer",
            DbKind.Byte => "integer",
            DbKind.Int16 => "integer",
            DbKind.Int32 => "integer",
            DbKind.Int64 => "integer",
            DbKind.Guid => "text",
            DbKind.UtcInstant => "integer",
            DbKind.Decimal => "real",
            DbKind.AsciiString => "text",
            DbKind.UnicodeString => "text",
            DbKind.Bytes => "blob",
            DbKind.BinaryPayload => "blob",
            _ => throw new InvalidOperationException($"Unhandled DbKind {c.Kind} on {c.Property.DeclaringType?.Name}.{c.Property.Name}"),
        };

    public override string RenderDefault(DbDefault d, ColumnModel c) =>
        d switch
        {
            DbDefault.None => "",
            DbDefault.UtcNow => " DEFAULT (CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER))",
            DbDefault.Zero => " DEFAULT 0",
            DbDefault.EmptyString => " DEFAULT ''",
            _ => throw new InvalidOperationException(
                $"Unhandled DbDefault {d} on {c.Property.DeclaringType?.Name}.{c.Property.Name} for SQLite"
            ),
        };

    public override string? PostTableSeed(string tableName) =>
        tableName == "namespaces"
            ? "-- Seed: reserved system namespace (id=1, 'sys') for cross-namespace audit events. Collation-neutral.\n"
                + $"INSERT OR IGNORE INTO {SchemaPlaceholder}.namespaces (id, name, status_code, description, created_at_utc, modified_at_utc)\n"
                + $"VALUES (1, 'sys', {(byte)JobNamespaceStatusCode.Active}, 'Reserved system namespace for cross-namespace audit events.', CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER), CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER));"
            : null;

    // Never reached: every identity-backed key is a sole integer PK folded inline as the
    // AUTOINCREMENT rowid alias (InlineIntegerPrimaryKey).
    public override string RenderIdentity() =>
        throw new InvalidOperationException("SQLite renders surrogate keys inline as the AUTOINCREMENT rowid alias.");

    // STRICT enforces column type affinity (catches a wrong-typed insert at the DB layer). It permits
    // only INT/INTEGER/REAL/TEXT/BLOB/ANY, which is why DbKind.Decimal renders as REAL, not numeric.
    public override string TableTrailingOptions(EntityModel e) => " STRICT";

    public override string PrimaryKeyTrailingOptions(DbPrimaryKeySpec pk) => "";

    public override void EmitProviderColumnChecks(StringBuilder sb, EntityModel e)
    {
        // SQLite stores all integers as 64-bit; emit the 0..255 range CHECK for plain Byte columns so
        // out-of-range ids fail at the DB layer (parity with the Postgres smallint range check). Coded
        // (enum-backed) byte columns are excluded: their enum IN (...) CHECK already constrains the value.
        foreach (var c in e.Columns.Where(c => c.Kind == DbKind.Byte && !c.IsCoded))
        {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"    , CONSTRAINT ck_{e.TableName}_{c.Name}_byte CHECK ({c.Name} BETWEEN 0 AND 255)"
            );
        }

        // SQLite BLOB is unbounded; enforce the declared Size via length() per bounded Bytes column.
        foreach (var c in e.Columns.Where(c => c.Kind == DbKind.Bytes))
        {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"    , CONSTRAINT ck_{e.TableName}_{c.Name}_bytes CHECK (length({c.Name}) <= {c.Size})"
            );
        }
    }

    public override string MigrationStamp(int version, string name) =>
        $"INSERT INTO {SchemaPlaceholder}.migrations (version, name, installed_schema)\n"
        + $"VALUES ({version}, '{PersistedMigrationName(version, name)}', '{SchemaPlaceholder}')\n"
        + "ON CONFLICT (version) DO NOTHING;";
}
