using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Acta.Generators.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Acta.Generators.Relational;

/// <summary>
/// AOT-first schema generator. Emits per-entity <c>ActaSchema.{Entity}Table</c> accessors, the
/// <c>ActaSchema.Entities</c> manifest, a typeof-switch
/// <c>For&lt;T&gt;()</c> dispatcher, and ordinal <c>EntityBinder</c> bodies.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ActaSchemaGenerator : IIncrementalGenerator
{
    private const string DbTableAttr = "Acta.Relational.Schema.DbTableAttribute";
    private const string DbColumnAttr = "Acta.Relational.Schema.DbColumnAttribute";
    private const string DbIgnoreAttr = "Acta.Relational.Schema.DbIgnoreAttribute";
    private const string DbPrimaryKeyAttr = "Acta.Relational.Schema.DbPrimaryKeyAttribute";
    private const string DbIndexAttr = "Acta.Relational.Schema.DbIndexAttribute";
    private const string DbUniqueIndexAttr = "Acta.Relational.Schema.DbUniqueIndexAttribute";
    private const string DbCheckAttr = "Acta.Relational.Schema.DbCheckAttribute";
    private const string DbForeignKeyAttr = "Acta.Relational.Schema.DbForeignKeyAttribute";
    private const string DbConcurrencyTokenAttr = "Acta.Relational.Schema.DbConcurrencyTokenAttribute";
    private const string CodeKindAttr = "Acta.CodeKindAttribute";

    // ACTA04xx diagnostics: see docs/internals/design.md § AOT and SQL parameter metadata policy.
    // Messages are fully formatted at the check site; every descriptor passes them through.
    private static readonly DiagnosticDescriptor SchemaDeclaration = new(
        id: "ACTA0401",
        title: "Schema declarations must be complete",
        messageFormat: "{0}",
        category: "ActaSchema",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor ColumnMapping = new(
        id: "ACTA0402",
        title: "Column mappings must match the CLR type",
        messageFormat: "{0}",
        category: "ActaSchema",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor ColumnDefault = new(
        id: "ACTA0403",
        title: "Column defaults must match the kind and allocation",
        messageFormat: "{0}",
        category: "ActaSchema",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var entities = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                DbTableAttr,
                predicate: static (n, _) => n is ClassDeclarationSyntax,
                transform: static (ctx, ct) => Transform(ctx, ct)
            )
            .Where(static e => e is not null)
            .Select(static (e, _) => e!.Value);

        // Report diagnostics per-entity (each entity carries its own list).
        context.RegisterSourceOutput(
            entities,
            static (spc, e) =>
            {
                foreach (var d in e.Diagnostics)
                {
                    spc.ReportDiagnostic(d.ToDiagnostic());
                }
            }
        );

        context.RegisterSourceOutput(entities.Collect(), static (spc, items) => Emit(spc, items));
    }

    // ============================================================================================
    // Transform
    // ============================================================================================

    private static EntityInfo? Transform(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
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

        // A PK-less entity still transforms so ACTA0401 can report it; Emit skips it for codegen.
        var pkAttr = cls.GetAttributes().FirstOrDefault(a => Match(a, DbPrimaryKeyAttr));
        var pkName = pkAttr is null ? "" : ReadNamedArg(pkAttr, "Name") as string ?? "";
        var pkColumns = pkAttr is null ? [] : ReadArrayNamedArg(pkAttr, "Columns");
        var pkManual = pkAttr is not null && ReadNamedArg(pkAttr, "Manual") is bool b && b;
        var pkOptimizeForSequentialKey = pkAttr is not null && ReadNamedArg(pkAttr, "OptimizeForSequentialKey") is bool osk && osk;

        var entityFqn = cls.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var columns = ImmutableArray.CreateBuilder<ColumnInfo>();
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();

        foreach (var member in cls.GetMembers())
        {
            if (member is not IPropertySymbol prop)
            {
                continue;
            }
            if (HasAttr(prop, DbIgnoreAttr))
            {
                continue;
            }
            var colAttr = prop.GetAttributes().FirstOrDefault(a => Match(a, DbColumnAttr));
            if (colAttr is null || colAttr.ConstructorArguments.Length < 1)
            {
                continue;
            }
            if (colAttr.ConstructorArguments[0].Value is not string colName)
            {
                continue;
            }

            // Explicit-vs-inferred is decided by constructor-arg count: [DbColumn("c")] (1 arg) is the
            // inferred enum form, [DbColumn("c", DbKind.X)] (2 args) declares a non-enum kind explicitly.
            var hasExplicitKind = colAttr.ConstructorArguments.Length >= 2;

            var size = ReadNamedArgInt(colAttr, "Size");
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

            // Coded-ness is inferred from the CLR property being an enum: enum properties omit the
            // DbKind and store their enum's underlying width; non-enum properties declare it explicitly.
            var enumType = nonNullableType as INamedTypeSymbol;
            var isEnum = enumType is { TypeKind: TypeKind.Enum };

            string? kindName;
            var isCoded = false;
            var isExtensible = false;
            string? enumTypeName = null;
            string? codeKind = null;

            if (isEnum)
            {
                enumTypeName = enumType!.Name;
                var ckAttr = enumType.GetAttributes().FirstOrDefault(a => Match(a, CodeKindAttr));
                if (ckAttr is not null && ckAttr.ConstructorArguments.Length > 0)
                {
                    codeKind = ckAttr.ConstructorArguments[0].Value as string;
                    isExtensible = ckAttr.NamedArguments.FirstOrDefault(a => a.Key == "Extensible").Value.Value is true;
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
                    continue;
                }

                isCoded = true;
                kindName = enumType.EnumUnderlyingType?.SpecialType switch
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
                    continue;
                }
            }
            else
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
                    continue;
                }
                kindName = EnumMemberName(colAttr.ConstructorArguments[1]);
                if (kindName is null)
                {
                    continue;
                }
            }

            // Concurrency / PK membership
            var isVersion = HasAttr(prop, DbConcurrencyTokenAttr);
            var isPk = pkColumns.Contains(colName);
            var isSolePk = pkColumns.Length == 1 && pkColumns[0] == colName;

            if ((kindName == "AsciiString" || kindName == "UnicodeString") && (size is null || size == 0))
            {
                diagnostics.Add(
                    new DiagnosticInfo(
                        ColumnMapping.Id,
                        propLoc,
                        $"[DbColumn] '{colName}' on '{cls.Name}' uses DbKind.{kindName} but specifies no Size; runtime parameter binding requires explicit Size"
                    )
                );
            }
            if (kindName == "Bytes" && (size is null || size == 0))
            {
                diagnostics.Add(
                    new DiagnosticInfo(
                        ColumnMapping.Id,
                        propLoc,
                        $"[DbColumn] '{colName}' on '{cls.Name}' uses DbKind.Bytes but specifies no Size; runtime parameter binding requires explicit Size"
                    )
                );
            }
            if (kindName == "Decimal" && (precision is null || scale is null))
            {
                diagnostics.Add(
                    new DiagnosticInfo(
                        ColumnMapping.Id,
                        propLoc,
                        $"[DbColumn] '{colName}' on '{cls.Name}' uses DbKind.Decimal but is missing Precision or Scale; runtime parameter binding requires both"
                    )
                );
            }
            if (!isCoded && !KindMatchesClr(kindName, nonNullableType))
            {
                diagnostics.Add(
                    new DiagnosticInfo(
                        ColumnMapping.Id,
                        propLoc,
                        $"[DbColumn] '{colName}' on '{cls.Name}' declares DbKind.{kindName} but the property CLR type is '{nonNullableFqn}'"
                    )
                );
            }

            // ACTA0403: the DEFAULT clause must suit the kind and must not sit on a column whose
            // value the provider allocates (identity), where it would never fire.
            if (defaultName != "None")
            {
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
                if (isSolePk && !pkManual && identityEligible)
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

            columns.Add(
                new ColumnInfo(
                    AccessorName: PascalCase(colName),
                    ColumnName: colName,
                    ClrPropertyName: prop.Name,
                    ClrPropertyTypeFqn: propTypeFqn,
                    NonNullableTypeFqn: nonNullableFqn,
                    KindName: kindName,
                    Size: size,
                    Precision: precision,
                    Scale: scale,
                    IsNullable: isNullable,
                    DefaultName: defaultName,
                    IsCoded: isCoded,
                    IsExtensible: isExtensible,
                    IsPrimaryKey: isPk,
                    IsSolePrimaryKey: isSolePk,
                    IsManualPrimaryKey: isPk && pkManual,
                    IsConcurrencyToken: isVersion,
                    EnumTypeName: enumTypeName,
                    CodeKind: codeKind,
                    Generated: generated
                )
            );
        }

        // Indexes
        var indexes = ImmutableArray.CreateBuilder<IndexInfo>();
        foreach (var ix in cls.GetAttributes().Where(a => Match(a, DbIndexAttr)))
        {
            indexes.Add(ReadIndex(ix, isUnique: false));
        }
        foreach (var ix in cls.GetAttributes().Where(a => Match(a, DbUniqueIndexAttr)))
        {
            indexes.Add(ReadIndex(ix, isUnique: true));
        }

        // Checks
        var checks = ImmutableArray.CreateBuilder<CheckInfo>();
        foreach (var ck in cls.GetAttributes().Where(a => Match(a, DbCheckAttr)))
        {
            var name = ReadNamedArg(ck, "Name") as string ?? "";
            var sql = ReadNamedArg(ck, "Sql") as string ?? "";
            checks.Add(new CheckInfo(name, sql));
        }

        // Foreign keys
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

        // ACTA0401: declaration completeness for everything visible on this entity alone; the
        // cross-entity facts (duplicate tables, FK targets) live in Emit.
        var classLocation = cls.Locations.FirstOrDefault();
        var classLoc = classLocation is null ? default : LocationInfo.From(classLocation);
        var columnNames = new HashSet<string>(columns.Select(c => c.ColumnName), StringComparer.Ordinal);

        if (pkAttr is null)
        {
            diagnostics.Add(
                new DiagnosticInfo(
                    SchemaDeclaration.Id,
                    classLoc,
                    $"[DbTable] '{tableName}' entity '{cls.Name}' declares no [DbPrimaryKey]; every entity carries an explicit primary key"
                )
            );
        }
        else
        {
            if (!pkName.StartsWith("pk_", StringComparison.Ordinal))
            {
                diagnostics.Add(
                    new DiagnosticInfo(
                        SchemaDeclaration.Id,
                        classLoc,
                        $"[DbPrimaryKey] '{pkName}' on '{cls.Name}' must carry the 'pk_' prefix"
                    )
                );
            }
            foreach (var col in pkColumns.Where(c => !columnNames.Contains(c)))
            {
                diagnostics.Add(
                    new DiagnosticInfo(
                        SchemaDeclaration.Id,
                        classLoc,
                        $"[DbPrimaryKey] '{pkName}' on '{cls.Name}' references unknown column '{col}'"
                    )
                );
            }
        }

        foreach (var ix in indexes)
        {
            var prefix = ix.IsUnique ? "ux_" : "ix_";
            if (!ix.Name.StartsWith(prefix, StringComparison.Ordinal))
            {
                diagnostics.Add(
                    new DiagnosticInfo(
                        SchemaDeclaration.Id,
                        classLoc,
                        $"Index '{ix.Name}' on '{cls.Name}' must carry the '{prefix}' prefix"
                    )
                );
            }
            foreach (var col in ix.Columns.Concat(ix.Includes).Where(c => !columnNames.Contains(c)))
            {
                diagnostics.Add(
                    new DiagnosticInfo(
                        SchemaDeclaration.Id,
                        classLoc,
                        $"Index '{ix.Name}' on '{cls.Name}' references unknown column '{col}'"
                    )
                );
            }
        }

        foreach (var ck in checks.Where(c => !c.Name.StartsWith("ck_", StringComparison.Ordinal)))
        {
            diagnostics.Add(
                new DiagnosticInfo(SchemaDeclaration.Id, classLoc, $"[DbCheck] '{ck.Name}' on '{cls.Name}' must carry the 'ck_' prefix")
            );
        }

        foreach (var fk in foreignKeys)
        {
            if (!fk.Name.StartsWith("fk_", StringComparison.Ordinal))
            {
                diagnostics.Add(
                    new DiagnosticInfo(
                        SchemaDeclaration.Id,
                        classLoc,
                        $"[DbForeignKey] '{fk.Name}' on '{cls.Name}' must carry the 'fk_' prefix"
                    )
                );
            }
            if (!columnNames.Contains(fk.Column))
            {
                diagnostics.Add(
                    new DiagnosticInfo(
                        SchemaDeclaration.Id,
                        classLoc,
                        $"[DbForeignKey] '{fk.Name}' on '{cls.Name}' references unknown local column '{fk.Column}'"
                    )
                );
            }
        }

        // ACTA0401, identifier length: PostgreSQL truncates identifiers past 63 bytes (NAMEDATALEN),
        // silently breaking cross-provider name parity. Identifiers may contain non-ASCII characters,
        // so the guard measures UTF-8 byte count directly rather than char count.
        // Synthetic names mirror SqlSchemaEmitter / provider dialects: coded columns get
        // ck_{table}_{column}[_code]; plain Byte columns ck_{table}_{column}_byte; Bytes columns
        // ck_{table}_{column}_bytes.
        // Scope: this guard covers entity-generated DDL identifiers only; hand-authored routine/view/TVP
        // names and provider auto-named objects (e.g. Postgres {table}_id_seq sequences) are outside its scope.
        const int MaxIdentifierLength = 63;
        var identifierNames = new List<string> { tableName };
        if (pkAttr is not null)
        {
            identifierNames.Add(pkName);
        }
        identifierNames.AddRange(columns.Select(c => c.ColumnName));
        identifierNames.AddRange(indexes.Select(i => i.Name));
        identifierNames.AddRange(checks.Select(c => c.Name));
        identifierNames.AddRange(foreignKeys.Select(f => f.Name));
        foreach (var c in columns)
        {
            if (c.IsCoded)
            {
                var codeSuffix = c.ColumnName.EndsWith("_code", StringComparison.Ordinal) ? "" : "_code";
                identifierNames.Add($"ck_{tableName}_{c.ColumnName}{codeSuffix}");
            }
            else if (c.KindName == "Byte")
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
                        classLoc,
                        $"Identifier '{name}' on '{cls.Name}' is {byteCount} bytes; the cross-provider limit is {MaxIdentifierLength} (PostgreSQL NAMEDATALEN)"
                    )
                );
            }
        }

        return new EntityInfo(
            EntityName: cls.Name,
            EntityFqn: entityFqn,
            TableName: tableName,
            PageCompression: pageCompression,
            PrimaryKeyName: pkName,
            PrimaryKeyColumns: pkColumns,
            PrimaryKeyManual: pkManual,
            PrimaryKeyOptimizeForSequentialKey: pkOptimizeForSequentialKey,
            Columns: columns.ToImmutable(),
            Indexes: indexes.ToImmutable(),
            Checks: checks.ToImmutable(),
            ForeignKeys: foreignKeys.ToImmutable(),
            Diagnostics: diagnostics.ToImmutable(),
            Location: classLoc
        );
    }

    private static bool KindMatchesClr(string? kindName, ITypeSymbol type)
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

    private static IndexInfo ReadIndex(AttributeData a, bool isUnique)
    {
        var name = ReadNamedArg(a, "Name") as string ?? "";
        var cols = ReadArrayNamedArg(a, "Columns");
        var incs = ReadArrayNamedArg(a, "Includes");
        var desc = ReadArrayNamedArg(a, "Descending");
        var filt = ReadNamedArg(a, "Filter") as string;
        var usage = ReadNamedArg(a, "Usage") as string ?? "";
        return new IndexInfo(name, cols, incs, desc, filt, usage, isUnique);
    }

    private static object? ReadNamedArg(AttributeData a, string name)
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

    private static int? ReadNamedArgInt(AttributeData a, string name)
    {
        var v = ReadNamedArg(a, name);
        return v is int i && i != 0 ? i : null;
    }

    // Resolve a named enum-typed arg (e.g. `Default = DbDefault.UtcNow`) by constant value, so
    // enum reordering doesn't break us.
    private static string? ReadNamedArgEnum(AttributeData a, string name)
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

    private static string? EnumMemberName(TypedConstant c)
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

    private static ImmutableArray<string> ReadArrayNamedArg(AttributeData a, string name)
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

    private static bool HasAttr(ISymbol symbol, string metadataName) => symbol.GetAttributes().Any(a => Match(a, metadataName));

    private static bool Match(AttributeData a, string metadataName) => a.AttributeClass?.ToDisplayString() == metadataName;

    private static (bool IsNullable, ITypeSymbol NonNullableType) AnalyzeProperty(IPropertySymbol prop)
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

    // ============================================================================================
    // Emit
    // ============================================================================================

    private static void Emit(SourceProductionContext spc, ImmutableArray<EntityInfo> entities)
    {
        if (entities.IsDefaultOrEmpty)
        {
            return;
        }

        var sorted = entities.Sort(static (a, b) => string.CompareOrdinal(a.EntityName, b.EntityName));

        // ACTA0401, cross-entity facts: table names collide, FK targets resolve.
        foreach (var group in sorted.GroupBy(e => e.TableName, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            foreach (var entity in group)
            {
                spc.ReportDiagnostic(
                    new DiagnosticInfo(
                        SchemaDeclaration.Id,
                        entity.Location,
                        $"[DbTable] '{group.Key}' is declared by multiple entities; table names are unique"
                    ).ToDiagnostic()
                );
            }
        }

        var byFqn = sorted.ToDictionary(e => e.EntityFqn, StringComparer.Ordinal);
        foreach (var entity in sorted)
        {
            foreach (var fk in entity.ForeignKeys)
            {
                if (!byFqn.TryGetValue(fk.TargetFqn, out var target))
                {
                    spc.ReportDiagnostic(
                        new DiagnosticInfo(
                            SchemaDeclaration.Id,
                            entity.Location,
                            $"[DbForeignKey] '{fk.Name}' on '{entity.EntityName}' targets '{fk.TargetFqn}', which is not a [DbTable] entity"
                        ).ToDiagnostic()
                    );
                }
                else if (!target.Columns.Any(c => c.ColumnName == fk.TargetColumn))
                {
                    spc.ReportDiagnostic(
                        new DiagnosticInfo(
                            SchemaDeclaration.Id,
                            entity.Location,
                            $"[DbForeignKey] '{fk.Name}' on '{entity.EntityName}' references unknown column '{fk.TargetColumn}' on '{target.EntityName}'"
                        ).ToDiagnostic()
                    );
                }
            }
        }

        // A PK-less entity already carries its ACTA0401; emitting accessors for it would not compile.
        var emittable = sorted.Where(e => !e.PrimaryKeyColumns.IsDefaultOrEmpty).ToImmutableArray();
        if (emittable.IsDefaultOrEmpty)
        {
            return;
        }

        spc.AddSource("ActaSchema.Generated.cs", EmitActaSchema(emittable));
        spc.AddSource("EntityBinder.Generated.cs", EmitEntityBinder(emittable));
    }

    // -------- ActaSchema.Generated.cs --------

    private static string EmitActaSchema(ImmutableArray<EntityInfo> entities)
    {
        var sb = new StringBuilder();
        GeneratorText.AutoGeneratedHeader(sb);
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine();
        sb.AppendLine("namespace Acta.Relational.Schema;");
        sb.AppendLine();
        sb.AppendLine("internal static partial class ActaSchema");
        sb.AppendLine("{");

        // --- Static singleton accessors ---
        foreach (var e in entities)
        {
            sb.AppendLine("    /// <summary>");
            sb.Append("    /// Strongly-typed accessor for <see cref=\"")
                .Append(e.EntityFqn)
                .Append("\"/> (<c>")
                .Append(e.TableName)
                .AppendLine("</c>).");
            sb.AppendLine("    /// </summary>");
            sb.Append("    public static ").Append(e.EntityName).Append("Table ").Append(e.EntityName).AppendLine(" { get; } = new();");
            sb.AppendLine();
        }

        // --- Entities manifest ---
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Every entity declared in this assembly, ordered by table name.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public static IReadOnlyList<DbEntitySpec> Entities { get; } = new DbEntitySpec[]");
        sb.AppendLine("    {");
        var byTable = entities.Sort(static (a, b) => string.CompareOrdinal(a.TableName, b.TableName));
        foreach (var e in byTable)
        {
            sb.Append("        ").Append(e.EntityName).AppendLine(".Entity,");
        }
        sb.AppendLine("    };");
        sb.AppendLine();

        // --- For<T> / For(Type) dispatchers ---
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Get the spec for an entity type.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public static DbEntitySpec For<TEntity>() where TEntity : class, IEntity =>");
        sb.AppendLine("        For(typeof(TEntity));");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Get the spec for an entity type by <see cref=\"Type\"/>.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public static DbEntitySpec For(Type entityType)");
        sb.AppendLine("    {");
        foreach (var e in entities)
        {
            sb.Append("        if (entityType == typeof(")
                .Append(e.EntityFqn)
                .Append(")) return ")
                .Append(e.EntityName)
                .AppendLine(".Entity;");
        }
        sb.AppendLine("        throw new InvalidOperationException(");
        sb.AppendLine("            $\"Type {entityType.FullName} is not a [DbTable] entity in the Acta assembly.\");");
        sb.AppendLine("    }");
        sb.AppendLine();

        // --- Per-entity Table classes ---
        foreach (var e in entities)
        {
            EmitTableClass(sb, e);
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void EmitTableClass(StringBuilder sb, EntityInfo e)
    {
        sb.Append("    public sealed class ").Append(e.EntityName).AppendLine("Table");
        sb.AppendLine("    {");

        // Column properties (pre-declared so the entity literal below can reference them)
        foreach (var c in e.Columns)
        {
            sb.Append("        public DbColumnSpec<")
                .Append(c.ClrPropertyTypeFqn)
                .Append("> ")
                .Append(c.AccessorName)
                .AppendLine(" { get; }");
        }
        sb.AppendLine();

        // The DbEntitySpec literal: assembled in the constructor (after columns are bound).
        sb.AppendLine("        public DbEntitySpec Entity { get; }");
        sb.AppendLine();
        sb.AppendLine("        /// <summary>");
        sb.Append("        /// <c>").Append(e.TableName).AppendLine("</c>");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        public string Table => Entity.TableName;");
        sb.AppendLine();

        sb.Append("        public ").Append(e.EntityName).AppendLine("Table()");
        sb.AppendLine("        {");

        // Column literals
        foreach (var c in e.Columns)
        {
            EmitColumnLiteral(sb, e, c);
        }
        sb.AppendLine();

        // Entity literal
        sb.AppendLine("            Entity = new DbEntitySpec");
        sb.AppendLine("            {");
        sb.Append("                ClrType = typeof(").Append(e.EntityFqn).AppendLine("),");
        sb.Append("                TableName = \"").Append(e.TableName).AppendLine("\",");
        if (e.PageCompression)
        {
            sb.AppendLine("                PageCompression = true,");
        }
        sb.AppendLine("                Columns = new DbColumnSpec[]");
        sb.AppendLine("                {");
        foreach (var c in e.Columns)
        {
            sb.Append("                    ").Append(c.AccessorName).AppendLine(",");
        }
        sb.AppendLine("                },");
        // PK
        sb.Append("                PrimaryKey = new DbPrimaryKeySpec(\"").Append(e.PrimaryKeyName).Append("\", new string[] { ");
        for (int i = 0; i < e.PrimaryKeyColumns.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            sb.Append('"').Append(e.PrimaryKeyColumns[i]).Append('"');
        }
        sb.Append(" }, ").Append(e.PrimaryKeyManual ? "true" : "false").Append(')');
        if (e.PrimaryKeyOptimizeForSequentialKey)
        {
            sb.Append(" { OptimizeForSequentialKey = true }");
        }
        sb.AppendLine(",");
        // Indexes
        sb.AppendLine("                Indexes = new DbIndexSpec[]");
        sb.AppendLine("                {");
        foreach (var ix in e.Indexes)
        {
            EmitIndexLiteral(sb, ix);
        }
        sb.AppendLine("                },");
        // Checks
        sb.AppendLine("                Checks = new DbCheckSpec[]");
        sb.AppendLine("                {");
        foreach (var ck in e.Checks)
        {
            sb.Append("                    new DbCheckSpec(\"")
                .Append(ck.Name)
                .Append("\", ")
                .Append(StringLiteral(ck.Sql))
                .AppendLine("),");
        }
        sb.AppendLine("                },");
        // FKs
        sb.AppendLine("                ForeignKeys = new DbForeignKeySpec[]");
        sb.AppendLine("                {");
        foreach (var fk in e.ForeignKeys)
        {
            sb.Append("                    new DbForeignKeySpec(\"")
                .Append(fk.Name)
                .Append("\", \"")
                .Append(fk.Column)
                .Append("\", typeof(")
                .Append(fk.TargetFqn)
                .Append("), \"")
                .Append(fk.TargetColumn)
                .Append("\", DbForeignKeyAction.")
                .Append(fk.OnDeleteName)
                .AppendLine("),");
        }
        sb.AppendLine("                },");
        sb.AppendLine("            };");

        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static void EmitColumnLiteral(StringBuilder sb, EntityInfo e, ColumnInfo c)
    {
        sb.Append("            ").Append(c.AccessorName).Append(" = new DbColumnSpec<").Append(c.ClrPropertyTypeFqn).AppendLine(">(");
        sb.AppendLine("                new DbColumnSpec(");
        sb.Append("                    Name: \"").Append(c.ColumnName).AppendLine("\",");
        sb.Append("                    Kind: DbKind.").Append(c.KindName).AppendLine(",");
        sb.Append("                    Size: ").Append(c.Size?.ToString(CultureInfo.InvariantCulture) ?? "null").AppendLine(",");
        sb.Append("                    Precision: ").Append(c.Precision?.ToString(CultureInfo.InvariantCulture) ?? "null").AppendLine(",");
        sb.Append("                    Scale: ").Append(c.Scale?.ToString(CultureInfo.InvariantCulture) ?? "null").AppendLine(",");
        sb.Append("                    IsNullable: ").Append(c.IsNullable ? "true" : "false").AppendLine(",");
        sb.Append("                    Default: DbDefault.").Append(c.DefaultName).AppendLine(",");
        sb.Append("                    IsCoded: ").Append(c.IsCoded ? "true" : "false").AppendLine(",");
        sb.Append("                    IsExtensible: ").Append(c.IsExtensible ? "true" : "false").AppendLine(",");
        sb.Append("                    IsPrimaryKey: ").Append(c.IsPrimaryKey ? "true" : "false").AppendLine(",");
        sb.Append("                    IsSolePrimaryKey: ").Append(c.IsSolePrimaryKey ? "true" : "false").AppendLine(",");
        sb.Append("                    IsManualPrimaryKey: ").Append(c.IsManualPrimaryKey ? "true" : "false").AppendLine(",");
        sb.Append("                    IsConcurrencyToken: ").Append(c.IsConcurrencyToken ? "true" : "false").AppendLine(",");
        sb.Append("                    EnumTypeName: ").Append(StringLiteral(c.EnumTypeName)).AppendLine(",");
        sb.Append("                    CodeKind: ").Append(StringLiteral(c.CodeKind)).AppendLine(",");
        sb.Append("                    ClrPropertyName: ").Append(StringLiteral(c.ClrPropertyName)).AppendLine(",");
        sb.Append("                    Generated: ").Append(StringLiteral(c.Generated)).AppendLine("),");
        sb.Append("                \"").Append(e.TableName).AppendLine("\",");
        sb.Append("                \"").Append(c.ColumnName).AppendLine("\",");
        sb.Append("                \"p_").Append(c.ColumnName).AppendLine("\");");
    }

    private static void EmitIndexLiteral(StringBuilder sb, IndexInfo ix)
    {
        sb.Append("                    new DbIndexSpec(");
        sb.AppendLine();
        sb.Append("                        Name: \"").Append(ix.Name).AppendLine("\",");
        sb.Append("                        Columns: new string[] { ");
        for (int i = 0; i < ix.Columns.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            sb.Append('"').Append(ix.Columns[i]).Append('"');
        }
        sb.AppendLine(" },");
        sb.Append("                        Includes: ").Append(StringArrayLiteralOrNull(ix.Includes)).AppendLine(",");
        sb.Append("                        Descending: ").Append(StringArrayLiteralOrNull(ix.Descending)).AppendLine(",");
        sb.Append("                        Filter: ").Append(StringLiteral(ix.Filter)).AppendLine(",");
        sb.Append("                        Usage: ").Append(StringLiteral(ix.Usage)).AppendLine(",");
        sb.Append("                        IsUnique: ").Append(ix.IsUnique ? "true" : "false").AppendLine("),");
    }

    private static string StringArrayLiteralOrNull(ImmutableArray<string> arr)
    {
        if (arr.IsDefaultOrEmpty)
        {
            return "null";
        }
        var inner = string.Join(", ", arr.Select(s => "\"" + s + "\""));
        return "new string[] { " + inner + " }";
    }

    private static string StringLiteral(string? s)
    {
        return s is null ? "null" : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    // -------- EntityBinder.Generated.cs --------

    private static string EmitEntityBinder(ImmutableArray<EntityInfo> entities)
    {
        var sb = new StringBuilder();
        GeneratorText.AutoGeneratedHeader(sb);
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Data.Common;");
        sb.AppendLine("using Acta.Relational.Schema;");
        sb.AppendLine("using Acta.Relational.Commands;");
        sb.AppendLine();
        sb.AppendLine("namespace Acta.Relational.Schema;");
        sb.AppendLine();
        sb.AppendLine("internal static partial class EntityBinder");
        sb.AppendLine("{");

        // Dispatcher
        sb.AppendLine("    /// <summary>Materialize one entity instance from an ordinal-typed reader.");
        sb.AppendLine("    /// Columns are read in <see cref=\"DbEntitySpec.Columns\"/> order.</summary>");
        sb.AppendLine("    public static TEntity Bind<TEntity>(DbDataReader reader) where TEntity : class, IEntity");
        sb.AppendLine("    {");
        foreach (var e in entities)
        {
            sb.Append("        if (typeof(TEntity) == typeof(")
                .Append(e.EntityFqn)
                .Append(")) return (TEntity)(object)Bind")
                .Append(e.EntityName)
                .AppendLine("(reader);");
        }
        sb.AppendLine("        throw new InvalidOperationException(");
        sb.AppendLine("            $\"No generated binder for {typeof(TEntity).FullName}\");");
        sb.AppendLine("    }");
        sb.AppendLine();

        // Per-entity binders
        foreach (var e in entities)
        {
            EmitBinder(sb, e);
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void EmitBinder(StringBuilder sb, EntityInfo e)
    {
        sb.Append("    private static ").Append(e.EntityFqn).Append(" Bind").Append(e.EntityName).AppendLine("(DbDataReader r)");
        sb.AppendLine("    {");
        sb.Append("        return new ").Append(e.EntityFqn).AppendLine();
        sb.AppendLine("        {");
        for (int i = 0; i < e.Columns.Length; i++)
        {
            var c = e.Columns[i];
            sb.Append("            ").Append(c.ClrPropertyName).Append(" = ").Append(BindExpression(c, i)).AppendLine(",");
        }
        sb.AppendLine("        };");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static string BindExpression(ColumnInfo c, int ordinal)
    {
        // For nullable columns, wrap the typed expression in `r.IsDBNull(i) ? null : (...)`.
        var typed = BindTypedExpression(c, ordinal);
        return c.IsNullable ? "r.IsDBNull(" + ordinal + ") ? null : " + typed : typed;
    }

    // Convert.To* on narrow numerics keeps reads provider-tolerant (tinyint vs smallint).
    private static string BindTypedExpression(ColumnInfo c, int ordinal)
    {
        var ord = ordinal.ToString(CultureInfo.InvariantCulture);

        var readExpr = c.KindName switch
        {
            "Boolean" => "Convert.ToBoolean(r.GetValue(" + ord + "))",
            "Byte" => "Convert.ToByte(r.GetValue(" + ord + "))",
            "Int16" => "Convert.ToInt16(r.GetValue(" + ord + "))",
            "Int32" => "Convert.ToInt32(r.GetValue(" + ord + "))",
            "Int64" => "Convert.ToInt64(r.GetValue(" + ord + "))",
            "Guid" => "r.GetGuid(" + ord + ")",
            "UtcInstant" => "DbCellCoercion.GetDateTimeUtc(r, " + ord + ")",
            "Decimal" => "Convert.ToDecimal(r.GetValue(" + ord + "))",
            "AsciiString" or "UnicodeString" => "r.GetString(" + ord + ")",
            "Bytes" or "BinaryPayload" => "(byte[])r.GetValue(" + ord + ")",
            _ => "throw new InvalidOperationException(\"Unmapped DbKind." + c.KindName + " at ordinal " + ord + "\")",
        };

        // Coded columns store their enum's underlying numeric width; cast the read value to the enum.
        return c.IsCoded ? "(" + c.NonNullableTypeFqn + ")" + readExpr : readExpr;
    }

    // ============================================================================================
    // Wire types
    // ============================================================================================

    private readonly record struct EntityInfo(
        string EntityName,
        string EntityFqn,
        string TableName,
        bool PageCompression,
        string PrimaryKeyName,
        ImmutableArray<string> PrimaryKeyColumns,
        bool PrimaryKeyManual,
        bool PrimaryKeyOptimizeForSequentialKey,
        ImmutableArray<ColumnInfo> Columns,
        ImmutableArray<IndexInfo> Indexes,
        ImmutableArray<CheckInfo> Checks,
        ImmutableArray<ForeignKeyInfo> ForeignKeys,
        ImmutableArray<DiagnosticInfo> Diagnostics,
        LocationInfo Location
    );

    /// <summary>
    /// Equatable diagnostic payload: the incremental pipeline replays it on cache hits.
    /// </summary>
    private readonly record struct DiagnosticInfo(string DescriptorId, LocationInfo Location, string Message)
    {
        public Diagnostic ToDiagnostic()
        {
            var descriptor = DescriptorId switch
            {
                "ACTA0401" => SchemaDeclaration,
                "ACTA0402" => ColumnMapping,
                "ACTA0403" => ColumnDefault,
                _ => throw new InvalidOperationException($"Unknown ACTA04xx descriptor '{DescriptorId}'."),
            };
            return Diagnostic.Create(descriptor, Location.ToLocation(), Message);
        }
    }

    /// <summary>
    /// Equatable snapshot of <see cref="Location"/>; raw <see cref="Location"/> references the
    /// host compilation and breaks incremental caching.
    /// </summary>
    private readonly record struct LocationInfo(string FilePath, TextSpan TextSpan, LinePositionSpan LineSpan)
    {
        public static LocationInfo From(Location location)
        {
            var line = location.GetLineSpan();
            return new LocationInfo(line.Path, location.SourceSpan, line.Span);
        }

        public Location ToLocation() => string.IsNullOrEmpty(FilePath) ? Location.None : Location.Create(FilePath, TextSpan, LineSpan);
    }

    private readonly record struct ColumnInfo(
        string AccessorName,
        string ColumnName,
        string ClrPropertyName,
        string ClrPropertyTypeFqn,
        string NonNullableTypeFqn,
        string KindName,
        int? Size,
        int? Precision,
        int? Scale,
        bool IsNullable,
        string DefaultName,
        bool IsCoded,
        bool IsExtensible,
        bool IsPrimaryKey,
        bool IsSolePrimaryKey,
        bool IsManualPrimaryKey,
        bool IsConcurrencyToken,
        string? EnumTypeName,
        string? CodeKind,
        string? Generated
    );

    private readonly record struct IndexInfo(
        string Name,
        ImmutableArray<string> Columns,
        ImmutableArray<string> Includes,
        ImmutableArray<string> Descending,
        string? Filter,
        string Usage,
        bool IsUnique
    );

    private readonly record struct CheckInfo(string Name, string Sql);

    private readonly record struct ForeignKeyInfo(string Name, string Column, string TargetFqn, string TargetColumn, string OnDeleteName);

    // snake_case -> PascalCase: status_code -> StatusCode
    private static string PascalCase(string snake)
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
