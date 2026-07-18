using Acta.Generators.Relational;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Acta.Tests.Generators;

public sealed class DbProjectionGeneratorTests
{
    private const string Stubs = """
        #nullable enable

        namespace Acta.Relational.Commands
        {
            using System;
            using System.Data;

            [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
            internal sealed class DbProjectionAttribute : Attribute
            {
                public DbProjectionAttribute(params Type[] projectionTypes) { }
            }

            internal static class DbCellCoercion
            {
                public static DateTime GetDateTimeUtc(this IDataRecord reader, int ordinal) => default;
            }
        }

        """;

    [Fact]
    public void Record_projection_emits_ordinal_binder()
    {
        var (result, compilation) = RunGenerator(
            """
            namespace GenTests;
            using System;
            using Acta.Relational.Commands;

            internal enum ProjectionAction : byte
            {
                Inserted = 1,
            }

            [DbProjection]
            internal sealed record EnqueueOutcomeRow(int Ordinal, long JobId, Guid JobRef, ProjectionAction Action);
            """
        );

        AssertNoCompileErrors(compilation);
        var source = SingleGeneratedSource(result, "BindEnqueueOutcomeRow");
        Assert.Contains("internal static partial class DbProjectionBinder", source);
        Assert.Contains("public static global::GenTests.EnqueueOutcomeRow BindEnqueueOutcomeRow(DbDataReader r)", source);
        Assert.Contains("Ordinal: Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture)", source);
        Assert.Contains("JobId: Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture)", source);
        Assert.Contains("JobRef: r.GetGuid(2)", source);
        Assert.Contains("Action: (global::GenTests.ProjectionAction)Convert.ToByte(r.GetValue(3), CultureInfo.InvariantCulture)", source);
        Assert.Contains("internal static void __ActaTryResolveDbProjection<T>(ref Func<DbDataReader, T>? read)", source);
        Assert.Contains("if (typeof(T) == typeof(global::GenTests.EnqueueOutcomeRow))", source);
        Assert.Contains("read = static r => (T)(object)BindEnqueueOutcomeRow(r);", source);

        var resolver = SingleGeneratedSource(result, "TryResolveGenerated");
        Assert.Contains("namespace Acta.Relational.Commands;", resolver);
        Assert.Contains("public static Func<DbDataReader, T> Resolve<T>()", resolver);
        Assert.Contains("global::GenTests.DbProjectionBinder.__ActaTryResolveDbProjection(ref read);", resolver);
    }

    [Fact]
    public void Assembly_projection_list_emits_binder_for_unannotated_type()
    {
        var (result, compilation) = RunGenerator(
            """
            using Acta.Relational.Commands;
            [assembly: DbProjection(typeof(GenTests.ExternalOutcome))]

            namespace GenTests;

            internal sealed record ExternalOutcome(int Id, string Name);
            """
        );

        AssertNoCompileErrors(compilation);
        var source = SingleGeneratedSource(result, "BindExternalOutcome");
        Assert.Contains("public static global::GenTests.ExternalOutcome BindExternalOutcome(DbDataReader r)", source);
    }

    [Fact]
    public void Class_projection_emits_nullable_bytes_and_utc_reads()
    {
        var (result, compilation) = RunGenerator(
            """
            namespace GenTests;
            using System;
            using Acta.Relational.Commands;

            [DbProjection]
            internal sealed class PayloadRow(
                int Id,
                string? Name,
                byte[] Payload,
                DateTime CreatedAtUtc,
                DateTime? AvailableAtUtc);
            """
        );

        AssertNoCompileErrors(compilation);
        var source = SingleGeneratedSource(result, "BindPayloadRow");
        Assert.Contains("Id: Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture)", source);
        Assert.Contains("Name: r.IsDBNull(1) ? null : r.GetString(1)", source);
        Assert.Contains("Payload: (byte[])r.GetValue(2)", source);
        Assert.Contains("CreatedAtUtc: r.GetDateTimeUtc(3)", source);
        Assert.Contains("AvailableAtUtc: r.IsDBNull(4) ? null : r.GetDateTimeUtc(4)", source);
    }

    [Fact]
    public void Private_nested_projection_emits_binder_inside_containing_type()
    {
        var (result, compilation) = RunGenerator(
            """
            namespace GenTests;
            using Acta.Relational.Commands;

            internal static partial class Operation
            {
                public static object Read(System.Data.Common.DbDataReader reader) => DbProjectionBinder.BindPrivateRow(reader);

                [DbProjection]
                private sealed record PrivateRow(int Id);
            }
            """
        );

        AssertNoCompileErrors(compilation);
        var source = SingleGeneratedSource(result, "BindPrivateRow");
        Assert.Contains("internal static partial class Operation", source);
        Assert.Contains("private static partial class DbProjectionBinder", source);
        Assert.Contains("public static global::GenTests.Operation.PrivateRow BindPrivateRow(DbDataReader r)", source);
        Assert.Contains("internal static void __ActaTryResolveDbProjection<T>(ref Func<DbDataReader, T>? read)", source);
        Assert.Contains("if (typeof(T) == typeof(global::GenTests.Operation.PrivateRow))", source);
        Assert.Contains("read = static r => (T)(object)DbProjectionBinder.BindPrivateRow(r);", source);

        var resolver = SingleGeneratedSource(result, "TryResolveGenerated");
        Assert.Contains("global::GenTests.Operation.__ActaTryResolveDbProjection(ref read);", resolver);
    }

    [Fact]
    public void Unsupported_projection_parameter_errors_ACTA0501()
    {
        var (result, _) = RunGenerator(
            """
            namespace GenTests;
            using System;
            using Acta.Relational.Commands;

            [DbProjection]
            internal sealed record BadProjection(Uri Value);
            """
        );

        var error = Assert.Single(result.Diagnostics, d => d.Id == "ACTA0501");
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
    }

    private static (GeneratorDriverRunResult Result, Compilation Compilation) RunGenerator(string projectionSource)
    {
        var referencePaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

        var compilation = CSharpCompilation.Create(
            "DbProjectionGeneratorTests",
            [CSharpSyntaxTree.ParseText(Stubs, parseOptions), CSharpSyntaxTree.ParseText(projectionSource, parseOptions)],
            referencePaths.Select(p => MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
        );

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new DbProjectionGenerator().AsSourceGenerator()],
            parseOptions: parseOptions
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);
        return (driver.GetRunResult(), outputCompilation);
    }

    private static string SingleGeneratedSource(GeneratorDriverRunResult result, string requiredText) =>
        Assert.Single(result.GeneratedTrees.Select(t => t.GetText().ToString()), s => s.Contains(requiredText));

    private static void AssertNoCompileErrors(Compilation compilation)
    {
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(e => e.ToString())));
    }
}
