namespace Acta.Emit.Features.Migrations;

internal readonly record struct TableMember(string Table, string Name);

/// <summary>One dialect's routines and views whose body is new or changed, and those gone from the source SQL.</summary>
internal sealed record ObjectChanges(IReadOnlyList<string> Changed, IReadOnlyList<string> Removed);

/// <summary>
/// Additive structural delta between two <see cref="SchemaSnapshot"/>s. Added tables and members are drafted
/// as DDL, and a new or changed routine or view as its whole body; everything non-additive (removals,
/// signature changes, PK changes, code-family membership changes) is surfaced as a warning for the engineer
/// to hand-edit.
/// </summary>
internal sealed record SchemaDiff(
    IReadOnlyList<string> AddedTables,
    IReadOnlyList<TableMember> AddedColumns,
    IReadOnlyList<TableMember> AddedIndexes,
    IReadOnlyList<TableMember> AddedChecks,
    IReadOnlyList<TableMember> AddedForeignKeys,
    IReadOnlyList<string> ChangedTableOptions,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, ObjectChanges> Objects
)
{
    internal bool IsEmpty =>
        AddedTables.Count == 0
        && AddedColumns.Count == 0
        && AddedIndexes.Count == 0
        && AddedChecks.Count == 0
        && AddedForeignKeys.Count == 0
        && ChangedTableOptions.Count == 0
        && Warnings.Count == 0
        && Objects.Values.All(o => o.Changed.Count == 0 && o.Removed.Count == 0);

    /// <summary>The table warnings, then one per removed routine or view of any dialect, for the console.</summary>
    internal IEnumerable<string> AllWarnings =>
        Warnings.Concat(
            Objects.SelectMany(d =>
                d.Value.Removed.Select(name => $"{name} ({d.Key}): removed from the source SQL; not drafted, write its DROP by hand.")
            )
        );

    internal static SchemaDiff Compute(SchemaSnapshot from, SchemaSnapshot to)
    {
        var fromEntities = from.Entities.ToDictionary(e => e.Table, StringComparer.Ordinal);
        var toEntities = to.Entities.ToDictionary(e => e.Table, StringComparer.Ordinal);

        var addedTables = new List<string>();
        var addedColumns = new List<TableMember>();
        var addedIndexes = new List<TableMember>();
        var addedChecks = new List<TableMember>();
        var addedForeignKeys = new List<TableMember>();
        var changedTableOptions = new List<string>();
        var warnings = new List<string>();

        foreach (var (table, toEntity) in toEntities.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!fromEntities.TryGetValue(table, out var fromEntity))
            {
                addedTables.Add(table);
                continue;
            }

            DiffMembers(table, fromEntity.Columns, toEntity.Columns, c => c.Name, c => c.Signature, "column", addedColumns, warnings);
            DiffMembers(table, fromEntity.Indexes, toEntity.Indexes, i => i.Name, i => i.Signature, "index", addedIndexes, warnings);
            DiffMembers(table, fromEntity.Checks, toEntity.Checks, c => c.Name, c => c.Sql, "check", addedChecks, warnings);
            DiffMembers(
                table,
                fromEntity.ForeignKeys,
                toEntity.ForeignKeys,
                f => f.Name,
                f => f.Signature,
                "foreign key",
                addedForeignKeys,
                warnings
            );

            if (fromEntity.Options != toEntity.Options)
            {
                changedTableOptions.Add(table);
            }

            if (fromEntity.PrimaryKey != toEntity.PrimaryKey)
            {
                warnings.Add(
                    $"{table}: primary key changed ('{fromEntity.PrimaryKey}' -> '{toEntity.PrimaryKey}'); not drafted, hand-edit required."
                );
            }
        }

        foreach (var table in fromEntities.Keys.Where(t => !toEntities.ContainsKey(t)).OrderBy(t => t, StringComparer.Ordinal))
        {
            warnings.Add($"{table}: table removed; not drafted, hand-edit required.");
        }

        var objects = new SortedDictionary<string, ObjectChanges>(StringComparer.Ordinal);
        foreach (var dialect in from.Objects.Keys.Union(to.Objects.Keys))
        {
            var before = from.Objects.GetValueOrDefault(dialect) ?? new Dictionary<string, string>();
            var after = to.Objects.GetValueOrDefault(dialect) ?? new Dictionary<string, string>();
            objects[dialect] = new ObjectChanges(
                after
                    .Where(o => !before.TryGetValue(o.Key, out var previous) || previous != o.Value)
                    .Select(o => o.Key)
                    .Order(StringComparer.Ordinal)
                    .ToList(),
                before.Keys.Where(n => !after.ContainsKey(n)).Order(StringComparer.Ordinal).ToList()
            );
        }

        return new SchemaDiff(addedTables, addedColumns, addedIndexes, addedChecks, addedForeignKeys, changedTableOptions, warnings, objects);
    }

    private static void DiffMembers<T>(
        string table,
        IReadOnlyList<T> from,
        IReadOnlyList<T> to,
        Func<T, string> name,
        Func<T, string> signature,
        string kind,
        List<TableMember> added,
        List<string> warnings
    )
    {
        var fromByName = from.ToDictionary(name, signature, StringComparer.Ordinal);
        var toNames = new HashSet<string>(to.Select(name), StringComparer.Ordinal);

        foreach (var member in to)
        {
            var n = name(member);
            if (!fromByName.TryGetValue(n, out var oldSig))
            {
                added.Add(new TableMember(table, n));
            }
            else if (oldSig != signature(member))
            {
                warnings.Add($"{table}.{n}: {kind} definition changed; not drafted, hand-edit required.");
            }
        }

        foreach (var n in fromByName.Keys.Where(n => !toNames.Contains(n)).OrderBy(n => n, StringComparer.Ordinal))
        {
            warnings.Add($"{table}.{n}: {kind} removed; not drafted, hand-edit required.");
        }
    }
}
