using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using static Acta.Generators.ActaSchemaGenerator;

namespace Acta.Generators;

/// <summary>Reads one <c>[DbTable]</c> entity: its columns, keys, indexes, checks, and foreign keys, with the declaration checks.</summary>
internal static class EntityReader
{
    /// <summary>
    /// ============================================================================================
    /// Transform
    /// ============================================================================================
    /// </summary>
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
        bool OptimizeForSequentialKey
    );

    private static PrimaryKeyDecl ReadPrimaryKey(INamedTypeSymbol cls)
    {
        var pkAttr = cls.GetAttributes().FirstOrDefault(a => Match(a, DbPrimaryKeyAttr));
        return pkAttr is null
            ? new PrimaryKeyDecl(false, "", [], false, false)
            : new PrimaryKeyDecl(
                true,
                ReadNamedArg(pkAttr, "Name") as string ?? "",
                ReadArrayNamedArg(pkAttr, "Columns"),
                ReadNamedArg(pkAttr, "Manual") is bool b && b,
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
        if (HasAttr(prop, DbIgnoreAttr))
        {
            return null;
        }
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
        var precision = ReadNamedArgInt(colAttr, "Precision");
        var scale = ReadNamedArgInt(colAttr, "Scale");
        var defaultName = ReadNamedArgEnum(colAttr, "Default") ?? "None";
        var generated = ReadNamedArg(colAttr, "Generated") as string;

        // Property type analysis
        var (isNullable, nonNullableType) = AnalyzeProperty(prop);
        var propTypeFqn = prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var nonNullableFqn = nonNullableType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // ACTADB diagnostics: gate every column before emit so a violation surfaces at build
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

        var column = new ColumnInfo(
            AccessorName: PascalCase(colName),
            ColumnName: colName,
            ClrPropertyName: prop.Name,
            ClrPropertyTypeFqn: propTypeFqn,
            NonNullableTypeFqn: nonNullableFqn,
            KindName: kind.Name,
            Size: size,
            Precision: precision,
            Scale: scale,
            IsNullable: isNullable,
            DefaultName: defaultName,
            IsCoded: kind.IsCoded,
            IsPrimaryKey: isPk,
            IsSolePrimaryKey: isSolePk,
            IsManualPrimaryKey: isPk && pk.Manual,
            IsConcurrencyToken: isVersion,
            EnumTypeName: kind.EnumTypeName,
            CodeKind: kind.CodeKind,
            Generated: generated,
            DecoderFqn: isEnum ? CodeFamilyNames.DecoderFqn(enumType!) : null,
            CaseExact: caseExact
        );
        EntityChecks.CheckColumn(cls, column, nonNullableType, pk.Manual, propLoc, diagnostics);
        return column;
    }

    /// <summary>A column's storage kind and, for an enum property, its code family.</summary>
    private readonly record struct ColumnKind(string Name, bool IsCoded, string? EnumTypeName, string? CodeKind);

    /// <summary>
    /// Coded-ness is inferred from the CLR property being an enum: enum properties omit the DbKind and store
    /// their enum's underlying width; non-enum properties declare it explicitly. Null when a diagnostic rejects
    /// the column or its declared kind does not resolve.
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

        var kindName = enumType.EnumUnderlyingType?.SpecialType switch
        {
            SpecialType.System_Byte => "Byte",
            SpecialType.System_Int16 => "Int16",
            SpecialType.System_Int32 => "Int32",
            _ => null,
        };
        if (kindName is null)
        {
            diagnostics.Add(
                new DiagnosticInfo(
                    ColumnMapping.Id,
                    propLoc,
                    $"[DbColumn] '{colName}' on '{cls.Name}' is enum-backed but '{enumTypeName}' has neither byte, short, nor int underlying type"
                )
            );
            return null;
        }
        return new ColumnKind(kindName, true, enumTypeName, codeKind);
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

    internal static bool KindMatchesClr(string? kindName, ITypeSymbol type)
    {
        if (kindName is null)
        {
            return true;
        }
        if (type is INamedTypeSymbol nt && nt.TypeKind == TypeKind.Enum)
        {
            // Enum-backed columns are validated through inference and underlying-width checks. This
            // path handles explicitly declared non-enum kinds, but accepts narrow numerics defensively.
            return kindName is "Byte" or "Int16" or "Int32" or "Int64";
        }
        return kindName switch
        {
            "Boolean" => type.SpecialType == SpecialType.System_Boolean,
            "Byte" => type.SpecialType == SpecialType.System_Byte,
            "Int16" => type.SpecialType == SpecialType.System_Int16,
            "Int32" => type.SpecialType == SpecialType.System_Int32,
            "Int64" => type.SpecialType == SpecialType.System_Int64,
            "Guid" => type.ToDisplayString() == "System.Guid",
            "UtcInstant" => type.ToDisplayString() == "System.DateTime",
            "Decimal" => type.SpecialType == SpecialType.System_Decimal,
            "AsciiString" or "UnicodeString" => type.SpecialType == SpecialType.System_String,
            "Bytes" or "BinaryPayload" => type is IArrayTypeSymbol a && a.ElementType.SpecialType == SpecialType.System_Byte,
            _ => true, // unknown kind: let the downstream check report it
        };
    }

    internal static IndexInfo ReadIndex(AttributeData a, bool isUnique)
    {
        var name = ReadNamedArg(a, "Name") as string ?? "";
        var cols = ReadArrayNamedArg(a, "Columns");
        var incs = ReadArrayNamedArg(a, "Includes");
        var desc = ReadArrayNamedArg(a, "Descending");
        var filt = ReadNamedArg(a, "Filter") as string;
        var usage = ReadNamedArg(a, "Usage") as string ?? "";
        var optimizeForSequentialKey = ReadNamedArg(a, "OptimizeForSequentialKey") is bool osk && osk;
        return new IndexInfo(name, cols, incs, desc, filt, usage, isUnique, optimizeForSequentialKey);
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
