using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using static Acta.Generators.ActaSchemaGenerator;
using static Acta.Generators.EntityReader;

namespace Acta.Generators;

/// <summary>The ACTA04xx checks on one entity: each column's mapping and default, the declaration, and identifier length.</summary>
internal static class EntityChecks
{
    /// <summary>ACTA0402 and ACTA0403 on one column: its size, precision, CLR type, and DEFAULT clause.</summary>
    internal static void CheckColumn(
        INamedTypeSymbol cls,
        ColumnInfo c,
        ITypeSymbol nonNullableType,
        bool pkManual,
        LocationInfo propLoc,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics
    )
    {
        var colName = c.ColumnName;
        var kindName = c.KindName;
        if ((kindName == "AsciiString" || kindName == "UnicodeString") && (c.Size is null || c.Size == 0))
        {
            diagnostics.Add(
                new DiagnosticInfo(
                    ColumnMapping.Id,
                    propLoc,
                    $"[DbColumn] '{colName}' on '{cls.Name}' uses DbKind.{kindName} but specifies no Size; runtime parameter binding requires explicit Size"
                )
            );
        }
        if (kindName == "Bytes" && (c.Size is null || c.Size == 0))
        {
            diagnostics.Add(
                new DiagnosticInfo(
                    ColumnMapping.Id,
                    propLoc,
                    $"[DbColumn] '{colName}' on '{cls.Name}' uses DbKind.Bytes but specifies no Size; runtime parameter binding requires explicit Size"
                )
            );
        }
        if (kindName == "Decimal" && (c.Precision is null || c.Scale is null))
        {
            diagnostics.Add(
                new DiagnosticInfo(
                    ColumnMapping.Id,
                    propLoc,
                    $"[DbColumn] '{colName}' on '{cls.Name}' uses DbKind.Decimal but is missing Precision or Scale; runtime parameter binding requires both"
                )
            );
        }
        if (!c.IsCoded && !KindMatchesClr(kindName, nonNullableType))
        {
            diagnostics.Add(
                new DiagnosticInfo(
                    ColumnMapping.Id,
                    propLoc,
                    $"[DbColumn] '{colName}' on '{cls.Name}' declares DbKind.{kindName} but the property CLR type is '{c.NonNullableTypeFqn}'"
                )
            );
        }

        // ACTA0403: the DEFAULT clause must suit the kind and must not sit on a column whose
        // value the provider allocates (identity), where it would never fire.
        var defaultName = c.DefaultName;
        if (defaultName == "None")
        {
            return;
        }
        var compatible = defaultName switch
        {
            "UtcNow" => kindName == "UtcInstant",
            "Zero" => kindName is "Byte" or "Int16" or "Int32" or "Int64" or "Decimal",
            "EmptyString" => kindName is "AsciiString" or "UnicodeString",
            "NewGuid" => kindName == "Guid",
            _ => true,
        };
        if (!compatible)
        {
            diagnostics.Add(
                new DiagnosticInfo(
                    ColumnDefault.Id,
                    propLoc,
                    $"[DbColumn] '{colName}' on '{cls.Name}' pairs DbDefault.{defaultName} with DbKind.{kindName}; the default is incompatible with the column type"
                )
            );
        }

        // Mirrors SqlSchemaEmitter.RenderAllocation / IsIdentityEligible.
        var identityEligible = kindName is "Byte" or "Int16" or "Int32" or "Int64";
        if (c.IsSolePrimaryKey && !pkManual && identityEligible)
        {
            diagnostics.Add(
                new DiagnosticInfo(
                    ColumnDefault.Id,
                    propLoc,
                    $"[DbColumn] '{colName}' on '{cls.Name}' carries DbDefault.{defaultName} but the value is provider-allocated (identity); the DEFAULT clause would never fire"
                )
            );
        }
    }

    /// <summary>ACTA0401: the primary key, index, check, and foreign-key declarations on the entity alone.</summary>
    internal static void CheckDeclaration(EntityInfo e, bool pkDeclared, ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        var classLoc = e.Location;
        var columnNames = new HashSet<string>(e.Columns.Select(c => c.ColumnName), StringComparer.Ordinal);

        if (!pkDeclared)
        {
            diagnostics.Add(
                new DiagnosticInfo(
                    SchemaDeclaration.Id,
                    classLoc,
                    $"[DbTable] '{e.TableName}' entity '{e.EntityName}' declares no [DbPrimaryKey]; every entity carries an explicit primary key"
                )
            );
        }
        else
        {
            if (!e.PrimaryKeyName.StartsWith("pk_", StringComparison.Ordinal))
            {
                diagnostics.Add(
                    new DiagnosticInfo(
                        SchemaDeclaration.Id,
                        classLoc,
                        $"[DbPrimaryKey] '{e.PrimaryKeyName}' on '{e.EntityName}' must carry the 'pk_' prefix"
                    )
                );
            }
            foreach (var col in e.PrimaryKeyColumns.Where(c => !columnNames.Contains(c)))
            {
                diagnostics.Add(
                    new DiagnosticInfo(
                        SchemaDeclaration.Id,
                        classLoc,
                        $"[DbPrimaryKey] '{e.PrimaryKeyName}' on '{e.EntityName}' references unknown column '{col}'"
                    )
                );
            }
        }

        foreach (var ix in e.Indexes)
        {
            var prefix = ix.IsUnique ? "ux_" : "ix_";
            if (!ix.Name.StartsWith(prefix, StringComparison.Ordinal))
            {
                diagnostics.Add(
                    new DiagnosticInfo(
                        SchemaDeclaration.Id,
                        classLoc,
                        $"Index '{ix.Name}' on '{e.EntityName}' must carry the '{prefix}' prefix"
                    )
                );
            }
            foreach (var col in ix.Columns.Concat(ix.Includes).Where(c => !columnNames.Contains(c)))
            {
                diagnostics.Add(
                    new DiagnosticInfo(
                        SchemaDeclaration.Id,
                        classLoc,
                        $"Index '{ix.Name}' on '{e.EntityName}' references unknown column '{col}'"
                    )
                );
            }
        }

        foreach (var ck in e.Checks.Where(c => !c.Name.StartsWith("ck_", StringComparison.Ordinal)))
        {
            diagnostics.Add(
                new DiagnosticInfo(SchemaDeclaration.Id, classLoc, $"[DbCheck] '{ck.Name}' on '{e.EntityName}' must carry the 'ck_' prefix")
            );
        }

        foreach (var fk in e.ForeignKeys)
        {
            if (!fk.Name.StartsWith("fk_", StringComparison.Ordinal))
            {
                diagnostics.Add(
                    new DiagnosticInfo(
                        SchemaDeclaration.Id,
                        classLoc,
                        $"[DbForeignKey] '{fk.Name}' on '{e.EntityName}' must carry the 'fk_' prefix"
                    )
                );
            }
            if (!columnNames.Contains(fk.Column))
            {
                diagnostics.Add(
                    new DiagnosticInfo(
                        SchemaDeclaration.Id,
                        classLoc,
                        $"[DbForeignKey] '{fk.Name}' on '{e.EntityName}' references unknown local column '{fk.Column}'"
                    )
                );
            }
        }
    }

    /// <summary>
    /// ACTA0401, identifier length: PostgreSQL truncates identifiers past 63 bytes (NAMEDATALEN),
    /// silently breaking cross-provider name parity. Identifiers may contain non-ASCII characters,
    /// so the guard measures UTF-8 byte count directly rather than char count. It covers entity-generated
    /// DDL identifiers only; hand-authored routine, view, and TVP names and provider auto-named objects
    /// (e.g. Postgres {table}_id_seq sequences) are outside its scope.
    /// </summary>
    internal static void CheckIdentifierLengths(EntityInfo e, bool pkDeclared, ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        const int MaxIdentifierLength = 63;
        var tableName = e.TableName;
        var identifierNames = new List<string> { tableName };
        if (pkDeclared)
        {
            identifierNames.Add(e.PrimaryKeyName);
        }
        identifierNames.AddRange(e.Columns.Select(c => c.ColumnName));
        identifierNames.AddRange(e.Indexes.Select(i => i.Name));
        identifierNames.AddRange(e.Checks.Select(c => c.Name));
        identifierNames.AddRange(e.ForeignKeys.Select(f => f.Name));
        // Synthetic names mirror the provider dialects: plain Byte columns get ck_{table}_{column}_byte and
        // Bytes columns ck_{table}_{column}_bytes; a code column carries no CHECK.
        foreach (var c in e.Columns)
        {
            if (c.KindName == "Byte" && !c.IsCoded)
            {
                identifierNames.Add($"ck_{tableName}_{c.ColumnName}_byte");
            }
            if (c.KindName == "Bytes")
            {
                identifierNames.Add($"ck_{tableName}_{c.ColumnName}_bytes");
            }
        }
        foreach (var name in identifierNames)
        {
            var byteCount = System.Text.Encoding.UTF8.GetByteCount(name);
            if (byteCount > MaxIdentifierLength)
            {
                diagnostics.Add(
                    new DiagnosticInfo(
                        SchemaDeclaration.Id,
                        e.Location,
                        $"Identifier '{name}' on '{e.EntityName}' is {byteCount} bytes; the cross-provider limit is {MaxIdentifierLength} (PostgreSQL NAMEDATALEN)"
                    )
                );
            }
        }
    }
}
