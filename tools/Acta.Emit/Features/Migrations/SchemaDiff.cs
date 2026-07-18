namespace Acta.Emit.Features.Migrations;

internal readonly record struct TableMember(string Table, string Name);

/// <summary>
/// Additive structural delta between two <see cref="SchemaSnapshot"/>s. Added objects are drafted as
/// DDL; everything non-additive (removals, signature changes, PK changes, code-family membership
/// changes) is surfaced as a <see cref="Warnings"/> line for the engineer to hand-edit.
/// </summary>
internal sealed record SchemaDiff(
    IReadOnlyList<string> AddedTables,
    IReadOnlyList<TableMember> AddedColumns,
    IReadOnlyList<TableMember> AddedIndexes,
    IReadOnlyList<TableMember> AddedChecks,
    IReadOnlyList<TableMember> AddedForeignKeys,
    IReadOnlyList<string> Warnings
)
{
    internal bool IsEmpty =>
        AddedTables.Count == 0
        && AddedColumns.Count == 0
        && AddedIndexes.Count == 0
        && AddedChecks.Count == 0
        && AddedForeignKeys.Count == 0
        && Warnings.Count == 0;

    internal static SchemaDiff Compute(SchemaSnapshot from, SchemaSnapshot to)
    {
        var fromEntities = from.Entities.ToDictionary(e => e.Table, StringComparer.Ordinal);
        var toEntities = to.Entities.ToDictionary(e => e.Table, StringComparer.Ordinal);

        var addedTables = new List<string>();
        var addedColumns = new List<TableMember>();
        var addedIndexes = new List<TableMember>();
        var addedChecks = new List<TableMember>();
        var addedForeignKeys = new List<TableMember>();
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

        DiffCodeFamilies(from, to, warnings);

        return new SchemaDiff(addedTables, addedColumns, addedIndexes, addedChecks, addedForeignKeys, warnings);
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

    private static void DiffCodeFamilies(SchemaSnapshot from, SchemaSnapshot to, List<string> warnings)
    {
        var fromByName = from.CodeFamilies.ToDictionary(f => f.CodeKind, f => f.Values, StringComparer.Ordinal);
        foreach (var family in to.CodeFamilies.OrderBy(f => f.CodeKind, StringComparer.Ordinal))
        {
            // A brand-new family arrives with its table/columns; their checks render with the column.
            if (!fromByName.TryGetValue(family.CodeKind, out var oldValues))
            {
                continue;
            }
            if (!oldValues.SequenceEqual(family.Values))
            {
                warnings.Add(
                    $"code family {family.CodeKind} frozen id/code/description contract changed; regenerate the synthetic check (ck_*) on columns using it. Not drafted, hand-edit required."
                );
            }
        }

        var toNames = new HashSet<string>(to.CodeFamilies.Select(f => f.CodeKind), StringComparer.Ordinal);
        foreach (var name in fromByName.Keys.Where(n => !toNames.Contains(n)).OrderBy(n => n, StringComparer.Ordinal))
        {
            warnings.Add($"code family {name} removed; not drafted, hand-edit required.");
        }
    }
}
