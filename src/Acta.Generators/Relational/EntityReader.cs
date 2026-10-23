using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using static Acta.Generators.ActaSchemaGenerator;
using static Acta.Generators.EntityReader;

namespace Acta.Generators;

/// <summary>Reads one <c>[DbTable]</c> entity: its columns, keys, indexes, checks, and foreign keys, with the declaration checks.</summary>
internal static class EntityReader
{
    internal static EntityInfo? Transform(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol cls)
        {
            return null;
        }

        var tableAttr = ctx.Attributes.FirstOrDefault();
        if (tableAttr is null || tableAttr.ConstructorArguments.Length == 0)
        {
            return null;
        }
        if (tableAttr.ConstructorArguments[0].Value is not string tableName)
        {
            return null;
        }
        var pageCompression = ReadNamedArg(tableAttr, "PageCompression") is bool pc && pc;
        var disableLockEscalation = ReadNamedArg(tableAttr, "DisableLockEscalation") is bool dle && dle;

        // A PK-less entity still transforms so ACTA0401 can report it; Emit skips it for codegen.
        var pk = ReadPrimaryKey(cls);
        var entityFqn = cls.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var columns = ImmutableArray.CreateBuilder<ColumnInfo>();
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        foreach (var member in cls.GetMembers())
        {
            if (member is IPropertySymbol prop && ReadColumn(cls, prop, pk, diagnostics) is { } column)
            {
                columns.Add(column);
            }
        }

        var indexes = ReadIndexes(cls);
        var checks = ReadChecks(cls);
        var foreignKeys = ReadForeignKeys(cls);

        // ACTA0401: declaration completeness for everything visible on this entity alone; the
        // cross-entity facts (duplicate tables, FK targets) live in Emit.
        var classLocation = cls.Locations.FirstOrDefault();
        var classLoc = classLocation is null ? default : LocationInfo.From(classLocation);
        var entity = new EntityInfo(
            EntityName: cls.Name,
            EntityFqn: entityFqn,
            TableName: tableName,
            PageCompression: pageCompression,
            DisableLockEscalation: disableLockEscalation,
            PrimaryKeyName: pk.Name,
            PrimaryKeyColumns: pk.Columns,
            PrimaryKeyManual: pk.Manual,
            PrimaryKeyIdentity: pk.Identity,
            PrimaryKeyOptimizeForSequentialKey: pk.OptimizeForSequentialKey,
            Columns: columns.ToImmutable(),
            Indexes: indexes,
            Checks: checks,
            ForeignKeys: foreignKeys,
            Diagnostics: default,
            Location: classLoc
        );
        EntityChecks.CheckDeclaration(entity, pk.Declared, diagnostics);
        EntityChecks.CheckIdentifierLengths(entity, pk.Declared, diagnostics);
        return entity with { Diagnostics = diagnostics.ToImmutable() };
    }

    /// <summary>The entity's <c>[DbPrimaryKey]</c>; an absent one reads as undeclared with no columns.</summary>
    internal readonly record struct PrimaryKeyDecl(
        bool Declared,
        string Name,
        ImmutableArray<string> Columns,
        bool Manual,
        string? Identity,
        bool OptimizeForSequentialKey
    );

    private static PrimaryKeyDecl ReadPrimaryKey(INamedTypeSymbol cls)
    {
        var pkAttr = cls.GetAttributes().FirstOrDefault(a => Match(a, DbPrimaryKeyAttr));
        return pkAttr is null
            ? new PrimaryKeyDecl(false, "", [], false, null, false)
            : new PrimaryKeyDecl(
                true,
                ReadNamedArg(pkAttr, "Name") as string ?? "",
                ReadArrayNamedArg(pkAttr, "Columns"),
                ReadNamedArg(pkAttr, "Manual") is bool b && b,
                ReadNamedArg(pkAttr, "Identity") as string,
                ReadNamedArg(pkAttr, "OptimizeForSequentialKey") is bool osk && osk
            );
    }

    /// <summary>One <c>[DbColumn]</c> property as a column, or null when it is not one or a diagnostic rejects it.</summary>
    private static ColumnInfo? ReadColumn(
        INamedTypeSymbol cls,
        IPropertySymbol prop,
        PrimaryKeyDecl pk,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics
    )
    {
        var colAttr = prop.GetAttributes().FirstOrDefault(a => Match(a, DbColumnAttr));
        if (colAttr is null || colAttr.ConstructorArguments.Length < 1)
        {
            return null;
        }
        if (colAttr.ConstructorArguments[0].Value is not string colName)
        {
            return null;
        }

        var size = ReadNamedArgInt(colAttr, "Size");
        var caseExact = ReadNamedArg(colAttr, "CaseExact") is bool ce && ce;
        var defaultName = ReadNamedArgEnum(colAttr, "Default") ?? "None";
        var generated = ReadNamedArg(colAttr, "Generated") as string;

        // Property type analysis
        var (isNullable, nonNullableType) = AnalyzeProperty(prop);
        var propTypeFqn = prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var nonNullableFqn = nonNullableType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Column diagnostics gate every column before emit so a violation surfaces at build
        // time on the property declaration rather than as an obscure runtime parameter-bind
        // failure deep in a provider executor.
        var propLocation = prop.Locations.FirstOrDefault();
        var propLoc = propLocation is null ? default : LocationInfo.From(propLocation);

        var enumType = nonNullableType as INamedTypeSymbol;
        var isEnum = enumType is { TypeKind: TypeKind.Enum };
        if (ResolveKind(cls, colName, colAttr, isEnum ? enumType : null, propLoc, diagnostics) is not { } kind)
        {
            return null;
        }

        // Concurrency / PK membership
        var isVersion = HasAttr(prop, DbConcurrencyTokenAttr);
        var isPk = pk.Columns.Contains(colName);
        var isSolePk = pk.Columns.Length == 1 && pk.Columns[0] == colName;
        var isIdentity =
            isPk && !pk.Manual && (kind.Name is "Byte" or "Int16" or "Int32" or "Int64") && (isSolePk || pk.Identity == colName);

        var column = new ColumnInfo(
            AccessorName: PascalCase(colName),
            ColumnName: colName,
            ClrPropertyName: prop.Name,
            ClrPropertyTypeFqn: propTypeFqn,
            NonNullableTypeFqn: nonNullableFqn,
            KindName: kind.Name,
            Size: size,
            IsNullable: isNullable,
            DefaultName: defaultName,
            IsCoded: kind.IsCoded,
            IsPrimaryKey: isPk,
            IsIdentity: isIdentity,
            IsConcurrencyToken: isVersion,
            EnumTypeName: kind.EnumTypeName,
            CodeKind: kind.CodeKind,
            Generated: generated,
            DecoderFqn: isEnum ? CodeFamilyNames.DecoderFqn(enumType!) : null,
            CaseExact: caseExact
        );
        EntityChecks.CheckColumn(cls, column, nonNullableType, propLoc, diagnostics);
        return column;
    }

    /// <summary>A column's storage kind and, for an enum property, its code family.</summary>
    private readonly record struct ColumnKind(string Name, bool IsCoded, string? EnumTypeName, string? CodeKind);

    /// <summary>
    /// Coded-ness is inferred from the CLR property being an enum: enum properties omit the DbKind and store
    /// a byte; non-enum properties declare it explicitly. Null when a diagnostic rejects the column or its
    /// declared kind does not resolve.
    /// </summary>
    private static ColumnKind? ResolveKind(
        INamedTypeSymbol cls,
        string colName,
        AttributeData colAttr,
        INamedTypeSymbol? enumType,
        LocationInfo propLoc,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics
    )
    {
        // Explicit-vs-inferred is decided by constructor-arg count: [DbColumn("c")] (1 arg) is the
        // inferred enum form, [DbColumn("c", DbKind.X)] (2 args) declares a non-enum kind explicitly.
        var hasExplicitKind = colAttr.ConstructorArguments.Length >= 2;
        if (enumType is null)
        {
            if (!hasExplicitKind)
            {
                diagnostics.Add(
                    new DiagnosticInfo(
                        ColumnMapping.Id,
                        propLoc,
                        $"[DbColumn] '{colName}' on '{cls.Name}' is a non-enum property and must specify a DbKind"
                    )
                );
                return null;
            }
            return EnumMemberName(colAttr.ConstructorArguments[1]) is { } declared ? new ColumnKind(declared, false, null, null) : null;
        }

        var enumTypeName = enumType.Name;
        string? codeKind = null;
        var ckAttr = enumType.GetAttributes().FirstOrDefault(a => Match(a, CodeKindAttr));
        if (ckAttr is not null && ckAttr.ConstructorArguments.Length > 0)
        {
            codeKind = ckAttr.ConstructorArguments[0].Value as string;
        }

        if (hasExplicitKind)
        {
            diagnostics.Add(
                new DiagnosticInfo(
                    ColumnMapping.Id,
                    propLoc,
                    $"[DbColumn] '{colName}' on '{cls.Name}' is an enum property; omit the DbKind argument (it is inferred from the enum's underlying type)"
                )
            );
            return null;
        }

        // The binder decodes a code column through its family's generated FromId, and every family is byte-backed (ACTA0201).
        if (codeKind is null || enumType.EnumUnderlyingType?.SpecialType != SpecialType.System_Byte)
        {
            diagnostics.Add(
                new DiagnosticInfo(
                    ColumnMapping.Id,
                    propLoc,
                    $"[DbColumn] '{colName}' on '{cls.Name}' is enum-backed but '{enumTypeName}' is not a byte-backed [CodeKind] family"
                )
            );
            return null;
        }
        return new ColumnKind("Byte", true, enumTypeName, codeKind);
    }

    private static ImmutableArray<IndexInfo> ReadIndexes(INamedTypeSymbol cls)
    {
        var indexes = ImmutableArray.CreateBuilder<IndexInfo>();
        foreach (var ix in cls.GetAttributes().Where(a => Match(a, DbIndexAttr)))
        {
            indexes.Add(ReadIndex(ix, isUnique: false));
        }
        foreach (var ix in cls.GetAttributes().Where(a => Match(a, DbUniqueIndexAttr)))
        {
            indexes.Add(ReadIndex(ix, isUnique: true));
        }
        return indexes.ToImmutable();
    }

    private static ImmutableArray<CheckInfo> ReadChecks(INamedTypeSymbol cls)
    {
        var checks = ImmutableArray.CreateBuilder<CheckInfo>();
        foreach (var ck in cls.GetAttributes().Where(a => Match(a, DbCheckAttr)))
        {
            var name = ReadNamedArg(ck, "Name") as string ?? "";
            var sql = ReadNamedArg(ck, "Sql") as string ?? "";
            checks.Add(new CheckInfo(name, sql));
        }
        return checks.ToImmutable();
    }

    private static ImmutableArray<ForeignKeyInfo> ReadForeignKeys(INamedTypeSymbol cls)
    {
        var foreignKeys = ImmutableArray.CreateBuilder<ForeignKeyInfo>();
        foreach (var fk in cls.GetAttributes().Where(a => Match(a, DbForeignKeyAttr)))
        {
            var name = ReadNamedArg(fk, "Name") as string ?? "";
            var column = ReadNamedArg(fk, "Column") as string ?? "";
            var targetColumn = ReadNamedArg(fk, "TargetColumn") as string ?? "";
            var onDeleteName = ReadNamedArgEnum(fk, "OnDelete") ?? "NoAction";

            string? targetFqn = null;
            var targetArg = fk.NamedArguments.FirstOrDefault(n => n.Key == "Target");
            if (targetArg.Value.Value is INamedTypeSymbol tgt)
            {
                targetFqn = tgt.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            }
            if (targetFqn is null)
            {
                continue;
            }

            foreignKeys.Add(new ForeignKeyInfo(name, column, targetFqn, targetColumn, onDeleteName));
        }
        return foreignKeys.ToImmutable();
    }

    /// <summary>Whether a declared kind fits its non-enum property; enum columns infer their kind instead.</summary>
    internal static bool KindMatchesClr(string kindName, ITypeSymbol type) =>
        kindName switch
        {
            "Boolean" => type.SpecialType == SpecialType.System_Boolean,
            "Byte" => type.SpecialType == SpecialType.System_Byte,
            "Int16" => type.SpecialType == SpecialType.System_Int16,
            "Int32" => type.SpecialType == SpecialType.System_Int32,
            "Int64" => type.SpecialType == SpecialType.System_Int64,
            "Guid" => type.ToDisplayString() == "System.Guid",
            "UtcInstant" => type.ToDisplayString() == "System.DateTime",
            "AsciiString" or "UnicodeString" => type.SpecialType == SpecialType.System_String,
            "BinaryPayload" => type is IArrayTypeSymbol a && a.ElementType.SpecialType == SpecialType.System_Byte,
            _ => true, // EnumMemberName yields DbKind member names only, and every one is listed above.
        };

    internal static IndexInfo ReadIndex(AttributeData a, bool isUnique)
    {
        var name = ReadNamedArg(a, "Name") as string ?? "";
        var cols = ReadArrayNamedArg(a, "Columns");
        var desc = ReadArrayNamedArg(a, "Descending");
        var include = ReadArrayNamedArg(a, "Include");
        var filt = ReadNamedArg(a, "Filter") as string;
        var usage = ReadNamedArg(a, "Usage") as string ?? "";
        return new IndexInfo(name, cols, desc, include, filt, usage, isUnique);
    }

    internal static object? ReadNamedArg(AttributeData a, string name)
    {
        foreach (var kv in a.NamedArguments)
        {
            if (kv.Key == name)
            {
                return kv.Value.Value;
            }
        }
        return null;
    }

    internal static int? ReadNamedArgInt(AttributeData a, string name)
    {
        var v = ReadNamedArg(a, name);
        return v is int i && i != 0 ? i : null;
    }

    /// <summary>
    /// Resolve a named enum-typed arg (e.g. `Default = DbDefault.UtcNow`) by constant value, so
    /// enum reordering doesn't break us.
    /// </summary>
    internal static string? ReadNamedArgEnum(AttributeData a, string name)
    {
        foreach (var kv in a.NamedArguments)
        {
            if (kv.Key == name)
            {
                return EnumMemberName(kv.Value);
            }
        }
        return null;
    }

    internal static string? EnumMemberName(TypedConstant c)
    {
        if (c.Type is not INamedTypeSymbol enumType || enumType.TypeKind != TypeKind.Enum)
        {
            return null;
        }
        var value = c.Value;
        foreach (var member in enumType.GetMembers())
        {
            if (member is IFieldSymbol f && f.HasConstantValue && Equals(f.ConstantValue, value))
            {
                return f.Name;
            }
        }
        return null;
    }

    internal static ImmutableArray<string> ReadArrayNamedArg(AttributeData a, string name)
    {
        foreach (var kv in a.NamedArguments)
        {
            if (kv.Key == name && !kv.Value.IsNull && !kv.Value.Values.IsDefaultOrEmpty)
            {
                var builder = ImmutableArray.CreateBuilder<string>(kv.Value.Values.Length);
                foreach (var item in kv.Value.Values)
                {
                    if (item.Value is string s)
                    {
                        builder.Add(s);
                    }
                }
                return builder.ToImmutable();
            }
        }
        return [];
    }

    internal static bool HasAttr(ISymbol symbol, string metadataName) => symbol.GetAttributes().Any(a => Match(a, metadataName));

    internal static bool Match(AttributeData a, string metadataName) => a.AttributeClass?.ToDisplayString() == metadataName;

    internal static (bool IsNullable, ITypeSymbol NonNullableType) AnalyzeProperty(IPropertySymbol prop)
    {
        var t = prop.Type;
        // Nullable value type: System.Nullable<T>
        if (t is INamedTypeSymbol nt && nt.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            return (true, nt.TypeArguments[0]);
        }
        // Value type, not nullable
        if (t.IsValueType)
        {
            return (false, t);
        }
        // Reference type: read NRT annotation. NullableAnnotation.Annotated means `T?`.
        return (t.NullableAnnotation == NullableAnnotation.Annotated, t);
    }

    /// <summary>
    /// snake_case -&gt; PascalCase: status_code -&gt; StatusCode
    /// </summary>
    internal static string PascalCase(string snake)
    {
        if (string.IsNullOrEmpty(snake))
        {
            return snake;
        }
        var sb = new StringBuilder(snake.Length);
        var nextUpper = true;
        foreach (var ch in snake)
        {
            if (ch == '_')
            {
                nextUpper = true;
                continue;
            }
            sb.Append(nextUpper ? char.ToUpperInvariant(ch) : ch);
            nextUpper = false;
        }
        return sb.ToString();
    }
}

/// <summary>The ACTA04xx checks on one entity: each column's mapping and default, the declaration, and identifier length.</summary>
internal static class EntityChecks
{
    /// <summary>ACTA0402 and ACTA0403 on one column: its size, CLR type, and DEFAULT clause.</summary>
    internal static void CheckColumn(
        INamedTypeSymbol cls,
        ColumnInfo c,
        ITypeSymbol nonNullableType,
        LocationInfo propLoc,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics
    )
    {
        var colName = c.ColumnName;
        var kindName = c.KindName;
        if ((kindName == "AsciiString" || kindName == "UnicodeString") && c.Size is null)
        {
            diagnostics.Add(
                new DiagnosticInfo(
                    ColumnMapping.Id,
                    propLoc,
                    $"[DbColumn] '{colName}' on '{cls.Name}' uses DbKind.{kindName} but specifies no Size; runtime parameter binding requires explicit Size"
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
            "Zero" => kindName is "Byte" or "Int16" or "Int32" or "Int64",
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

        if (c.IsIdentity)
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
            if (e.PrimaryKeyIdentity is { } identity && !e.Columns.Any(c => c.ColumnName == identity && c.IsIdentity))
            {
                diagnostics.Add(
                    new DiagnosticInfo(
                        SchemaDeclaration.Id,
                        classLoc,
                        $"[DbPrimaryKey] '{e.PrimaryKeyName}' on '{e.EntityName}' names Identity '{identity}', which is not an integer column of its key"
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
            foreach (var col in ix.Columns.Concat(ix.Include).Where(c => !columnNames.Contains(c)))
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
        // Synthetic names mirror the provider dialects: plain Byte columns get ck_{table}_{column}_byte; a code
        // column carries no CHECK.
        identifierNames.AddRange(
            e.Columns.Where(c => c.KindName == "Byte" && !c.IsCoded).Select(c => $"ck_{tableName}_{c.ColumnName}_byte")
        );
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
