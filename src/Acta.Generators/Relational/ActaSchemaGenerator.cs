using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static Acta.Generators.EntityReader;
using static Acta.Generators.SchemaSourceWriter;

namespace Acta.Generators;

/// <summary>
/// AOT-first schema generator. Emits per-entity <c>ActaSchema.{Entity}Table</c> accessors, the
/// <c>ActaSchema.Entities</c> manifest, a typeof-switch
/// <c>For&lt;T&gt;()</c> dispatcher, and ordinal <c>EntityBinder</c> bodies.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ActaSchemaGenerator : IIncrementalGenerator
{
    internal const string DbTableAttr = "Acta.Runtime.DbTableAttribute";
    internal const string DbColumnAttr = "Acta.Runtime.DbColumnAttribute";
    internal const string DbIgnoreAttr = "Acta.Runtime.DbIgnoreAttribute";
    internal const string DbPrimaryKeyAttr = "Acta.Runtime.DbPrimaryKeyAttribute";
    internal const string DbIndexAttr = "Acta.Runtime.DbIndexAttribute";
    internal const string DbUniqueIndexAttr = "Acta.Runtime.DbUniqueIndexAttribute";
    internal const string DbCheckAttr = "Acta.Runtime.DbCheckAttribute";
    internal const string DbForeignKeyAttr = "Acta.Runtime.DbForeignKeyAttribute";
    internal const string DbConcurrencyTokenAttr = "Acta.Runtime.DbConcurrencyTokenAttribute";
    internal const string CodeKindAttr = "Acta.CodeKindAttribute";

    /// <summary>
    /// ACTA04xx diagnostics: see docs/internals/design.md § AOT and SQL parameter metadata policy.
    /// Messages are fully formatted at the check site; every descriptor passes them through.
    /// </summary>
    internal static readonly DiagnosticDescriptor SchemaDeclaration = new(
        id: "ACTA0401",
        title: "Schema declarations must be complete",
        messageFormat: "{0}",
        category: "ActaSchema",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    internal static readonly DiagnosticDescriptor ColumnMapping = new(
        id: "ACTA0402",
        title: "Column mappings must match the CLR type",
        messageFormat: "{0}",
        category: "ActaSchema",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    internal static readonly DiagnosticDescriptor ColumnDefault = new(
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

    /// <summary>
    /// ============================================================================================
    /// Emit
    /// ============================================================================================
    /// </summary>
    internal static void Emit(SourceProductionContext spc, ImmutableArray<EntityInfo> entities)
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

    /// <summary>
    /// ============================================================================================
    /// Wire types
    /// ============================================================================================
    /// </summary>
    internal readonly record struct EntityInfo(
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
    internal readonly record struct DiagnosticInfo(string DescriptorId, LocationInfo Location, string Message)
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
    internal readonly record struct LocationInfo(string FilePath, TextSpan TextSpan, LinePositionSpan LineSpan)
    {
        public static LocationInfo From(Location location)
        {
            var line = location.GetLineSpan();
            return new LocationInfo(line.Path, location.SourceSpan, line.Span);
        }

        public Location ToLocation() => string.IsNullOrEmpty(FilePath) ? Location.None : Location.Create(FilePath, TextSpan, LineSpan);
    }

    internal readonly record struct ColumnInfo(
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
        bool IsPrimaryKey,
        bool IsSolePrimaryKey,
        bool IsManualPrimaryKey,
        bool IsConcurrencyToken,
        string? EnumTypeName,
        string? CodeKind,
        string? Generated,
        string? DecoderFqn
    );

    internal readonly record struct IndexInfo(
        string Name,
        ImmutableArray<string> Columns,
        ImmutableArray<string> Includes,
        ImmutableArray<string> Descending,
        string? Filter,
        string Usage,
        bool IsUnique,
        bool OptimizeForSequentialKey
    );

    internal readonly record struct CheckInfo(string Name, string Sql);

    internal readonly record struct ForeignKeyInfo(string Name, string Column, string TargetFqn, string TargetColumn, string OnDeleteName);
}
