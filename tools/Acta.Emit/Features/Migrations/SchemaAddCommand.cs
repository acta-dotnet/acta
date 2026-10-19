using System.Globalization;
using System.Text;
using Acta.Emit.Features.Docs;
using Acta.Emit.Shared;
using Acta.Emit.Shared.Model;
using Acta.Emit.Shared.Sql;

namespace Acta.Emit.Features.Migrations;

/// <summary>Emits the next migration <c>M{yyyyMMddHHmmss}_{name}</c> for every provider at one UTC instant: a delta
/// where the provider already has migrations, a full baseline where it has none (the first cut, or a late-joining
/// provider), then saves the snapshot and regenerates docs.</summary>
internal static class SchemaAddCommand
{
    internal static int Run(string? name, string? repoRoot = null, DateTime? now = null, bool force = false)
    {
        if (name is not null && !MigrationFiles.IsValidName(name))
        {
            Console.Error.WriteLine($"Invalid migration name '{name}'. Use snake_case: ^[a-z][a-z0-9_]*$.");
            return 2;
        }

        repoRoot ??= RepoRoot.Find();
        var stems = ProviderCatalog.All.ToDictionary(p => p, p => MigrationFiles.Stems(repoRoot, p.Token));
        var hasBaseline = stems.Values.Any(s => s.Count > 0);
        if (hasBaseline && name is null)
        {
            Console.Error.WriteLine("A baseline exists, so the migration needs a name: schema add --name <snake_case>.");
            return 2;
        }
        if (!hasBaseline && name is not null)
        {
            Console.Error.WriteLine("The baseline is init; drop --name.");
            return 2;
        }
        name ??= "init";

        // A name is used once: the runner refuses to start with two migrations of one name. --force replaces the one that
        // carries it, re-diffed against whatever snapshot is on disk, so redoing it starts with a git checkout of the
        // snapshot it was cut from.
        var taken = stems
            .SelectMany(kv =>
                kv.Value.Where(s => MigrationFiles.NameOf(s) == name).Select(s => MigrationFiles.PathFor(repoRoot, kv.Key.Token, s))
            )
            .ToList();
        if (taken.Count > 0 && !force)
        {
            Console.Error.WriteLine(
                $"A migration named '{name}' already exists ({Path.GetFileNameWithoutExtension(taken[0])}); pick another name, "
                    + "or pass --force to replace it."
            );
            return 2;
        }
        var withHistory = stems.Where(kv => kv.Value.Any(s => MigrationFiles.NameOf(s) != name)).Select(kv => kv.Key).ToHashSet();

        var live = SchemaModel.Discover();
        var objects = MigrationObjects.All(repoRoot);
        var to = SchemaSnapshot.Capture(live, objects);

        var snapshotPath = SnapshotFile.Path(repoRoot);
        var committed = File.Exists(snapshotPath) ? SchemaSnapshot.Load(snapshotPath) : null;
        // No committed snapshot means the first cut: everything renders as a full baseline, so a diff would only
        // report every object as added.
        var diff = committed is null ? null : SchemaDiff.Compute(committed, to);

        var anyNewProvider = withHistory.Count < ProviderCatalog.All.Count;
        if (diff is not null && diff.IsEmpty && !anyNewProvider)
        {
            if (taken.Count > 0)
            {
                Console.Error.WriteLine(
                    $"Nothing differs from the snapshot on disk, so --force would only delete '{name}'. "
                        + "Check out the snapshot it was cut from first."
                );
                return 2;
            }
            Console.WriteLine("  no schema changes since the committed snapshot; nothing to add.");
            return 0;
        }

        var stem = MigrationFiles.Stem(now ?? DateTime.UtcNow, name);

        // Render every provider's SQL first; only write once all succeed, so an emit failure mid-loop
        // can't leave a partial migration on disk with a stale snapshot.
        var pending = new List<(string Path, string Sql)>();
        foreach (var provider in ProviderCatalog.All)
        {
            var sql =
                diff is not null && withHistory.Contains(provider)
                    ? MigrationDeltaEmitter.EmitDelta(diff, live, objects[provider.Token], provider.Dialect, stem)
                    : SqlSchemaEmitter.EmitFullSchema(live, provider.Dialect, objects[provider.Token], stem);
            pending.Add((MigrationFiles.PathFor(repoRoot, provider.Token, stem), sql));
        }

        taken.ForEach(File.Delete);
        foreach (var (path, sql) in pending)
        {
            // Each provider owns its own Migrations folder, so create the target directory per file.
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, sql);
            Console.WriteLine($"  wrote {path}");
        }

        SchemaSnapshot.Save(to, snapshotPath);
        DocsCommand.Emit(repoRoot);

        if (diff is not null)
        {
            foreach (var warning in diff.AllWarnings)
            {
                Console.Error.WriteLine($"  WARNING: {warning}");
            }
        }
        return 0;
    }
}

/// <summary>
/// Renders a per-provider delta migration for an additive <see cref="SchemaDiff"/> by reusing the
/// full-schema emitter's fragment renderers against the live model, so a drafted statement matches
/// how the baseline renders the same object. Additive DDL is emitted directly, then each new or changed
/// routine and view of this dialect whole; non-additive changes (drops, renames, synthetic checks) are
/// surfaced as `-- WARNING`/`-- TODO` lines for the engineer to hand-edit. The migration self-stamps via
/// <c>MigrationStamp</c> as its last line.
/// </summary>
internal static class MigrationDeltaEmitter
{
    internal static string EmitDelta(SchemaDiff diff, SchemaModel live, DialectObjects objects, SqlDdlDialect dialect, string stem)
    {
        var byTable = live.Entities.ToDictionary(e => e.TableName, StringComparer.Ordinal);
        var token = dialect.ProviderToolName;
        var sb = new StringBuilder();

        foreach (var warning in diff.Warnings)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"-- WARNING: {warning}");
        }
        if (diff.Warnings.Count > 0)
        {
            sb.AppendLine();
        }
        SqlSchemaEmitter.AppendSessionOptions(sb, dialect);

        var addedTables = diff.AddedTables.ToHashSet(StringComparer.Ordinal);
        foreach (var entity in live.Entities.Where(e => addedTables.Contains(e.TableName)))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"-- new table: {entity.TableName}");
            sb.AppendLine(SqlSchemaEmitter.RenderCreateTableForDraft(entity, dialect, live));
            sb.AppendLine();
        }

        foreach (var (table, column) in diff.AddedColumns)
        {
            var col = byTable[table].Columns.First(c => c.Name == column);
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"ALTER TABLE {SqlDdlDialect.SchemaPlaceholder}.{table} ADD {SqlSchemaEmitter.RenderColumn(col, dialect)};"
            );
            // A code column carries no CHECK, so only a plain byte column needs its synthetic one.
            if (!col.IsCoded && col.Kind == DbKind.Byte)
            {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"-- TODO: add the synthetic ck_{table}_{col.Name} 0..255 range constraint for this {col.Kind} column (see the baseline)."
                );
            }
            if (col.IsGenerated)
            {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"-- TODO: {table}.{col.Name} is a STORED generated column; SQLite cannot ADD one via ALTER TABLE: rebuild the table by hand for the sqlite migration."
                );
            }
            else if (!col.IsNullable && col.Default == DbDefault.None)
            {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"-- TODO: {table}.{col.Name} is NOT NULL with no default; backfill existing rows in the same migration."
                );
            }
        }

        foreach (var (table, index) in diff.AddedIndexes)
        {
            var idx = byTable[table].Indexes.First(i => i.Name == index);
            sb.AppendLine(dialect.GuardIndexCreate(table, index, SqlSchemaEmitter.RenderIndex(table, idx, dialect)));
        }

        foreach (var (table, check) in diff.AddedChecks)
        {
            var chk = byTable[table].Checks.First(c => c.Name == check);
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"ALTER TABLE {SqlDdlDialect.SchemaPlaceholder}.{table} ADD CONSTRAINT {chk.Name} CHECK ({chk.Sql});"
            );
        }

        foreach (var (table, fkName) in diff.AddedForeignKeys)
        {
            var fk = byTable[table].ForeignKeys.First(f => f.Name == fkName);
            var targetTable = live.Entities.First(e => e.ClrType == fk.Target).TableName;
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"ALTER TABLE {SqlDdlDialect.SchemaPlaceholder}.{table} ADD CONSTRAINT {fk.Name} FOREIGN KEY ({fk.Column}) REFERENCES {dialect.ForeignKeyTargetTable(targetTable)} ({fk.TargetColumn}){RenderOnDelete(fk.OnDelete)};"
            );
        }

        foreach (var table in diff.ChangedTableOptions)
        {
            if (dialect.TableLockEscalation(table, byTable[table].DisableLockEscalation) is { } escalation)
            {
                sb.AppendLine(escalation);
            }
        }

        var changes = diff.Objects.GetValueOrDefault(token) ?? new ObjectChanges([], []);
        var drafted = objects.All.Where(o => changes.Changed.Contains(o.Name)).ToList();
        if (drafted.Count > 0)
        {
            // CREATE OR ALTER must open its batch on SQL Server, so whatever came before ends its own.
            SqlSchemaEmitter.EndBatch(sb, dialect);
            sb.AppendLine();
        }
        foreach (var o in drafted)
        {
            SqlSchemaEmitter.AppendObject(sb, o, dialect);
        }

        foreach (var removed in changes.Removed)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"-- TODO DROP {removed}: removed from the source SQL; write its drop here.");
        }

        // Self-stamp so the runner records this migration as applied (else it re-runs every boot).
        sb.AppendLine();
        sb.AppendLine(dialect.MigrationStamp(stem));
        SqlSchemaEmitter.AppendTerminator(sb, dialect);

        return sb.ToString();
    }

    private static string RenderOnDelete(DbForeignKeyAction action) =>
        action switch
        {
            DbForeignKeyAction.Cascade => " ON DELETE CASCADE",
            DbForeignKeyAction.SetNull => " ON DELETE SET NULL",
            _ => "",
        };
}
