using System.Globalization;
using System.Text;
using Acta.Emit.Shared.Model;

namespace Acta.Emit.Shared.Sql;

/// <summary>
/// Single-walker DDL emitter: provider-specific choices delegate to a <see cref="SqlDdlDialect"/>.
/// The emitted baseline is self-contained and idempotent: schema, tables, indexes,
/// provider types, routines, operator views, and its own history row.
/// </summary>
internal static class SqlSchemaEmitter
{
    // Renders the whole schema (tables, indexes, provider types, then every routine and view) as a
    // single migration recorded under <paramref name="stem"/>. Serves the first cut and a late-joining
    // provider's first migration.
    public static string EmitFullSchema(SchemaModel model, SqlDdlDialect dialect, DialectObjects objects, string stem)
    {
        var sb = new StringBuilder();
        AppendSessionOptions(sb, dialect);

        sb.AppendLine(dialect.CreateSchemaStatement);
        AppendTerminator(sb, dialect);
        sb.AppendLine();

        var emittedTables = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in TargetsFirst(model.Entities))
        {
            EmitTable(sb, entity, dialect, emittedTables);
            sb.AppendLine();
        }

        // Request-shape table types (TVPs) consumed by hot-path routines: a dialect literal, not part
        // of the entity model (so they don't pollute the data-model docs). MSSQL only; Postgres null.
        if (dialect.TrailingTypeDefinitions is { } typeDefs)
        {
            sb.AppendLine(typeDefs);
            sb.AppendLine();
        }

        foreach (var o in objects.All)
        {
            AppendObject(sb, o, dialect);
        }

        sb.AppendLine();
        sb.AppendLine(dialect.MigrationStamp(stem));
        AppendTerminator(sb, dialect);

        return sb.ToString();
    }

    // Renders one routine or view as its own statement, a batch of its own where the dialect has batches.
    // Shared with the delta emitter so a changed object drafts exactly as the baseline renders it.
    internal static void AppendObject(StringBuilder sb, SqlObject o, SqlDdlDialect dialect)
    {
        sb.AppendLine(o.Statement);
        AppendTerminator(sb, dialect);
        sb.AppendLine();
    }

    // Renders a full CREATE TABLE for a migration draft. Seeds the emitted-tables set with every live
    // table so inline FKs to already-existing tables pass the dependency-order guard.
    internal static string RenderCreateTableForDraft(EntityModel entity, SqlDdlDialect dialect, SchemaModel live)
    {
        var sb = new StringBuilder();
        var allTables = new HashSet<string>(live.Entities.Select(e => e.TableName), StringComparer.Ordinal);
        EmitTable(sb, entity, dialect, allTables);
        return sb.ToString().TrimEnd();
    }

    private static void EmitTable(StringBuilder sb, EntityModel e, SqlDdlDialect dialect, HashSet<string> emittedTables)
    {
        emittedTables.Add(e.TableName);

        // Headed by the table, as the DDL names it, so renaming a table model leaves the migration unchanged.
        sb.AppendLine(CultureInfo.InvariantCulture, $"-- {e.TableName}");
        if (dialect.TableGuardBegin(e.TableName) is { } guardBegin)
        {
            sb.AppendLine(guardBegin);
        }
        sb.AppendLine(CultureInfo.InvariantCulture, $"{dialect.CreateTableClause} {SqlDdlDialect.SchemaPlaceholder}.{e.TableName} (");

        var colDefs = e.Columns.Select(c => RenderColumn(c, dialect)).ToList();
        for (var i = 0; i < colDefs.Count; i++)
        {
            var comma = i < colDefs.Count - 1 ? "," : "";
            sb.AppendLine(CultureInfo.InvariantCulture, $"    {colDefs[i]}{comma}");
        }

        // The identity column is folded into the column itself on inline-PK dialects (SQLite rowid
        // alias) and is then the whole key; the table-level constraint would create a second index,
        // so it is skipped.
        if (!PkRenderedInline(e, dialect))
        {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"    , CONSTRAINT {e.PrimaryKey.Name} PRIMARY KEY ({string.Join(", ", e.PrimaryKey.Columns)}){dialect.PrimaryKeyTrailingOptions(e.PrimaryKey)}"
            );
        }

        foreach (var chk in e.Checks)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"    , CONSTRAINT {chk.Name} CHECK ({chk.Sql})");
        }

        // Provider-specific extras (the Postgres and SQLite byte range CHECKs).
        dialect.EmitProviderColumnChecks(sb, e);

        // FKs are table-level constraints (not post-CREATE ALTERs) so the whole table block stays
        // idempotent; that requires the target table to be emitted first.
        foreach (var fk in e.ForeignKeys)
        {
            var targetTable = ResolveTableName(fk.Target);
            if (!emittedTables.Contains(targetTable))
            {
                throw new InvalidOperationException($"FK {fk.Name} on {e.TableName} references {targetTable}, which is not emitted yet.");
            }
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"    , CONSTRAINT {fk.Name} FOREIGN KEY ({fk.Column}) REFERENCES {dialect.ForeignKeyTargetTable(targetTable)} ({fk.TargetColumn}){RenderOnDelete(fk.OnDelete)}"
            );
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"){dialect.TableTrailingOptions(e)};");
        if (e.DisableLockEscalation && dialect.TableLockEscalation(e.TableName, disabled: true) is { } escalation)
        {
            sb.AppendLine(escalation);
        }

        foreach (var idx in e.Indexes)
        {
            EmitIndex(sb, e.TableName, idx, dialect);
        }

        if (dialect.TableGuardEnd is { } guardEnd)
        {
            sb.AppendLine(guardEnd);
        }

        AppendTerminator(sb, dialect);

        // Bootstrap rows for this table (only namespaces seeds the reserved 'sys' row), emitted as
        // their own terminated block so a reset regenerates them instead of losing a hand-edit.
        if (dialect.PostTableSeed(e.TableName) is { } seed)
        {
            sb.AppendLine();
            foreach (var line in seed.Split('\n'))
            {
                sb.AppendLine(line.TrimEnd('\r'));
            }
            AppendTerminator(sb, dialect);
        }
    }

    private static void EmitIndex(StringBuilder sb, string tableName, DbIndexSpec idx, SqlDdlDialect dialect) =>
        sb.AppendLine(RenderIndex(tableName, idx, dialect));

    // Renders one standalone CREATE [UNIQUE] INDEX statement. Shared by baseline emission and
    // draft so an added index drafts exactly as the baseline would render it.
    internal static string RenderIndex(string tableName, DbIndexSpec idx, SqlDdlDialect dialect)
    {
        var cols = string.Join(", ", idx.Columns.Select(c => idx.Descending?.Contains(c) == true ? $"{c} DESC" : c));
        var include =
            idx.Include is { Count: > 0 } && dialect.SupportsIncludedColumns ? $" INCLUDE ({string.Join(", ", idx.Include)})" : "";
        var filter = idx.Filter is null ? "" : $" WHERE {idx.Filter}";
        return $"{dialect.CreateIndexClause(idx.IsUnique)} {dialect.IndexNameAndTable(idx.Name, tableName)} ({cols}){include}{filter};";
    }

    // True when the dialect folds this entity's identity column inline as its key (SQLite rowid alias).
    // Paired with the IsIdentity test in RenderColumn, so the column rendering and the table-level
    // constraint skip stay in lockstep.
    private static bool PkRenderedInline(EntityModel e, SqlDdlDialect dialect) =>
        dialect.InlineIntegerPrimaryKey && e.Columns.Any(c => c.IsIdentity);

    internal static string RenderColumn(ColumnModel c, SqlDdlDialect dialect)
    {
        if (c.IsGenerated)
        {
            return dialect.RenderGeneratedColumn(c);
        }

        if (dialect.InlineIntegerPrimaryKey && c.IsIdentity)
        {
            // SQLite rowid alias with AUTOINCREMENT: keep provider parity with SQL Server/Postgres
            // identities by never reusing ids after retention deletes a highest row.
            return $"{c.Name} integer PRIMARY KEY AUTOINCREMENT";
        }

        var typeSql = dialect.RenderType(c);
        var nullable = c.IsNullable ? "NULL" : "NOT NULL";
        var allocation = RenderAllocation(c, dialect);
        // DbDefault renders only when no allocation-default already occupies the slot.
        // The generator's ACTA0403 catches the conflicting combinations at compile time.
        var defaultClause = allocation.Length == 0 ? dialect.RenderDefault(c.Default, c) : "";
        return $"{c.Name} {typeSql}{allocation}{defaultClause} {nullable}";
    }

    // Provider-native PK allocation: the identity clause on the identity column, nothing elsewhere.
    private static string RenderAllocation(ColumnModel c, SqlDdlDialect dialect) => c.IsIdentity ? dialect.RenderIdentity() : "";

    private static string RenderOnDelete(DbForeignKeyAction action) =>
        action switch
        {
            DbForeignKeyAction.NoAction => "",
            DbForeignKeyAction.Cascade => " ON DELETE CASCADE",
            DbForeignKeyAction.SetNull => " ON DELETE SET NULL",
            _ => throw new InvalidOperationException($"Unhandled DbForeignKeyAction {action}"),
        };

    // Table-name order, except that a table follows every table its foreign keys target, because EmitTable writes each
    // key inline in the CREATE TABLE.
    private static IEnumerable<EntityModel> TargetsFirst(IReadOnlyList<EntityModel> entities)
    {
        var pending = entities.ToList();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var next =
                pending.Find(e => e.ForeignKeys.All(fk => placed.Contains(ResolveTableName(fk.Target))))
                ?? throw new InvalidOperationException(
                    $"Foreign keys form a cycle among {string.Join(", ", pending.Select(e => e.TableName))}."
                );
            pending.Remove(next);
            placed.Add(next.TableName);
            yield return next;
        }
    }

    private static string ResolveTableName(Type t)
    {
        var table = (DbTableAttribute?)Attribute.GetCustomAttribute(t, typeof(DbTableAttribute));
        return table?.Name ?? throw new InvalidOperationException($"FK target {t.Name} has no [DbTable].");
    }

    /// <summary>Opens a migration, baseline or delta, with the dialect's session-options batch where it has one.</summary>
    internal static void AppendSessionOptions(StringBuilder sb, SqlDdlDialect dialect)
    {
        if (dialect.SessionOptions is { } options)
        {
            sb.AppendLine(options);
            sb.AppendLine();
        }
    }

    internal static void AppendTerminator(StringBuilder sb, SqlDdlDialect dialect)
    {
        if (dialect.Terminator is { } t)
        {
            sb.AppendLine(t);
        }
    }

    // Ends the open batch unless the last statement already did, so a drafted table (which ends its own)
    // followed by a routine does not leave an empty batch in a file people review.
    internal static void EndBatch(StringBuilder sb, SqlDdlDialect dialect)
    {
        if (dialect.Terminator is { } t && !sb.ToString().TrimEnd().EndsWith(t, StringComparison.Ordinal))
        {
            sb.AppendLine(t);
        }
    }
}
