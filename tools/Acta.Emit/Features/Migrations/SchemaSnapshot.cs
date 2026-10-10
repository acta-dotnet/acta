using System.Text.Json;
using Acta.Emit.Shared.Model;
using Acta.Emit.Shared.Sql;

namespace Acta.Emit.Features.Migrations;

/// <summary>
/// Committed, tool-owned projection of the live schema model as of the last cut migration.
/// The diff baseline for the <c>schema add/amend</c> commands; never a competing schema authority.
/// Captures entities only: routine and view bodies are runtime-installed SQL
/// objects, not migration content.
/// </summary>
internal sealed record SchemaSnapshot(IReadOnlyList<EntitySnapshot> Entities)
{
    internal static SchemaSnapshot Capture(SchemaModel model)
    {
        var entities = model
            .Entities.Select(e => new EntitySnapshot(
                e.TableName,
                $"{e.PrimaryKey.Name}({string.Join(",", e.PrimaryKey.Columns)})|manual={e.PrimaryKey.Manual}|seq={e.PrimaryKey.OptimizeForSequentialKey}",
                e.Columns.Select(c => new ColumnSnapshot(c.Name, ColumnSignature(c))).ToList(),
                e.Indexes.Select(i => new IndexSnapshot(i.Name, IndexSignature(i))).OrderBy(i => i.Name, StringComparer.Ordinal).ToList(),
                e.Checks.Select(c => new CheckSnapshot(c.Name, c.Sql)).OrderBy(c => c.Name, StringComparer.Ordinal).ToList(),
                e.ForeignKeys.Select(f => new ForeignKeySnapshot(f.Name, ForeignKeySignature(f)))
                    .OrderBy(f => f.Name, StringComparer.Ordinal)
                    .ToList()
            ))
            .OrderBy(e => e.Table, StringComparer.Ordinal)
            .ToList();

        return new SchemaSnapshot(entities);
    }

    internal static void Save(SchemaSnapshot snapshot, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, SchemaSnapshotJsonContext.Default.SchemaSnapshot));

    internal static SchemaSnapshot Load(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), SchemaSnapshotJsonContext.Default.SchemaSnapshot)
        ?? throw new InvalidOperationException($"Empty or invalid schema snapshot at {path}.");

    // Coded identity is IsCoded and the stable CodeKind, never EnumTypeName, so a CLR enum rename is not drift;
    // member ids are not schema, since no column constrains its code values.
    private static string ColumnSignature(ColumnModel c) =>
        $"{c.Kind}|{c.Size}|{c.Precision}|{c.Scale}|{c.IsNullable}|{c.Default}|{c.Generated}|coded={c.IsCoded}|codeKind={c.CodeKind}|caseExact={c.CaseExact}";

    private static string IndexSignature(DbIndexSpec i) =>
        $"{(i.IsUnique ? "U" : "I")}|cols={string.Join(",", i.Columns)}"
        + $"|inc={(i.Includes is null ? "" : string.Join(",", i.Includes))}"
        + $"|desc={(i.Descending is null ? "" : string.Join(",", i.Descending))}"
        + $"|filter={i.Filter}"
        + $"|seq={i.OptimizeForSequentialKey}";

    // Signed by the target table, as the DDL references it, so renaming a table model is not a schema change.
    private static string ForeignKeySignature(DbForeignKeySpec f) =>
        $"{f.Column}->{ActaSchema.For(f.Target).TableName}.{f.TargetColumn}|{f.OnDelete}";
}

/// <summary>The committed snapshot file: <c>Current</c> is the state as of the tip migration;
/// <c>Previous</c> is the state as of the migration before it (so <c>amend</c> can diff the tip).</summary>
internal sealed record SnapshotPair(SchemaSnapshot Current, SchemaSnapshot? Previous)
{
    internal static void Save(SnapshotPair pair, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(pair, SchemaSnapshotJsonContext.Default.SnapshotPair));
    }

    internal static SnapshotPair Load(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), SchemaSnapshotJsonContext.Default.SnapshotPair)
        ?? throw new InvalidOperationException($"Empty or invalid snapshot pair at {path}.");
}

internal sealed record EntitySnapshot(
    string Table,
    string PrimaryKey,
    IReadOnlyList<ColumnSnapshot> Columns,
    IReadOnlyList<IndexSnapshot> Indexes,
    IReadOnlyList<CheckSnapshot> Checks,
    IReadOnlyList<ForeignKeySnapshot> ForeignKeys
);

internal sealed record ColumnSnapshot(string Name, string Signature);

internal sealed record IndexSnapshot(string Name, string Signature);

internal sealed record CheckSnapshot(string Name, string Sql);

internal sealed record ForeignKeySnapshot(string Name, string Signature);
