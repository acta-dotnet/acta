using System.Text.Json;
using Acta.Emit.Shared.Model;
using Acta.Emit.Shared.Sql;

namespace Acta.Emit.Features.Migrations;

/// <summary>
/// Committed, tool-owned projection of the live schema model as of the last cut migration.
/// The diff baseline for the <c>schema add</c> command; never a competing schema authority.
/// <c>Objects</c> maps each dialect token to its routines and views, name to <see cref="SqlObject.Hash"/>,
/// which is how <c>schema add</c> tells a changed body from an unchanged one.
/// </summary>
internal sealed record SchemaSnapshot(
    IReadOnlyList<EntitySnapshot> Entities,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Objects
)
{
    internal static SchemaSnapshot Capture(SchemaModel model, IReadOnlyDictionary<string, DialectObjects> objects)
    {
        var entities = model
            .Entities.Select(e => new EntitySnapshot(
                e.TableName,
                $"{e.PrimaryKey.Name}({string.Join(",", e.PrimaryKey.Columns)})|manual={e.PrimaryKey.Manual}|seq={e.PrimaryKey.OptimizeForSequentialKey}"
                    + (e.PrimaryKey.Identity is { } identity ? $"|identity={identity}" : ""),
                e.Columns.Select(c => new ColumnSnapshot(c.Name, ColumnSignature(c))).ToList(),
                e.Indexes.Select(i => new IndexSnapshot(i.Name, IndexSignature(i))).OrderBy(i => i.Name, StringComparer.Ordinal).ToList(),
                e.Checks.Select(c => new CheckSnapshot(c.Name, c.Sql)).OrderBy(c => c.Name, StringComparer.Ordinal).ToList(),
                e.ForeignKeys.Select(f => new ForeignKeySnapshot(f.Name, ForeignKeySignature(f)))
                    .OrderBy(f => f.Name, StringComparer.Ordinal)
                    .ToList(),
                e.DisableLockEscalation ? "lock_escalation=disable" : null
            ))
            .OrderBy(e => e.Table, StringComparer.Ordinal)
            .ToList();

        var hashes = new SortedDictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (token, dialectObjects) in objects)
        {
            hashes[token] = new SortedDictionary<string, string>(
                dialectObjects.All.ToDictionary(o => o.Name, o => o.Hash, StringComparer.Ordinal),
                StringComparer.Ordinal
            );
        }

        return new SchemaSnapshot(entities, hashes);
    }

    internal static void Save(SchemaSnapshot snapshot, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, SchemaSnapshotJsonContext.Default.SchemaSnapshot));
    }

    internal static SchemaSnapshot Load(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), SchemaSnapshotJsonContext.Default.SchemaSnapshot)
        ?? throw new InvalidOperationException($"Empty or invalid schema snapshot at {path}.");

    // Coded identity is IsCoded and the stable CodeKind, never EnumTypeName, so a CLR enum rename is not drift;
    // member ids are not schema, since no column constrains its code values. The literal 0|0 and seq=False hold the
    // places of a column's precision and scale and an index's sequential-key hint, which the model no longer has, so
    // the committed signatures stay unchanged; inc= is empty for an index without included columns.
    private static string ColumnSignature(ColumnModel c) =>
        $"{c.Kind}|{c.Size}|0|0|{c.IsNullable}|{c.Default}|{c.Generated}|coded={c.IsCoded}|codeKind={c.CodeKind}|caseExact={c.CaseExact}";

    private static string IndexSignature(DbIndexSpec i) =>
        $"{(i.IsUnique ? "U" : "I")}|cols={string.Join(",", i.Columns)}"
        + $"|inc={(i.Include is null ? "" : string.Join(",", i.Include))}"
        + $"|desc={(i.Descending is null ? "" : string.Join(",", i.Descending))}"
        + $"|filter={i.Filter}"
        + "|seq=False";

    // Signed by the target table, as the DDL references it, so renaming a table model is not a schema change.
    private static string ForeignKeySignature(DbForeignKeySpec f) =>
        $"{f.Column}->{ActaSchema.For(f.Target).TableName}.{f.TargetColumn}|{f.OnDelete}";
}

internal sealed record EntitySnapshot(
    string Table,
    string PrimaryKey,
    IReadOnlyList<ColumnSnapshot> Columns,
    IReadOnlyList<IndexSnapshot> Indexes,
    IReadOnlyList<CheckSnapshot> Checks,
    IReadOnlyList<ForeignKeySnapshot> ForeignKeys,
    // Table-level options a delta can apply in place; null when the table sets none.
    string? Options = null
);
