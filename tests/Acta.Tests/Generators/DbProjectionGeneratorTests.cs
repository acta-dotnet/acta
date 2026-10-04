using Acta.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Acta.Tests.Generators;

public sealed class DbProjectionGeneratorTests
{
    private const string Stubs = """
        #nullable enable

        namespace Acta.Runtime
        {
            using System;
            using System.Data;

            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
            internal sealed class DbProjectionAttribute : Attribute
            {
                public bool ByName { get; set; }
            }

            internal static class DbCellCoercion
            {
                public static DateTime GetDateTimeUtc(this IDataRecord reader, int ordinal) => default;
            }
        }

        """;

    [Fact]
    public void Record_projection_binds_by_position_by_default()
    {
        var (result, compilation) = RunGenerator(
            """
            namespace GenTests;
            using System;
            using Acta.Runtime;

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
        Assert.Contains("public static global::GenTests.EnqueueOutcomeRow BindEnqueueOutcomeRow(DbDataReader r, int[] o)", source);
        Assert.Contains("Ordinal: Convert.ToInt32(r.GetValue(o[0]), CultureInfo.InvariantCulture)", source);
        Assert.Contains("JobId: Convert.ToInt64(r.GetValue(o[1]), CultureInfo.InvariantCulture)", source);
        Assert.Contains("JobRef: r.GetGuid(o[2])", source);
        Assert.Contains(
            "Action: (global::GenTests.ProjectionAction)Convert.ToByte(r.GetValue(o[3]), CultureInfo.InvariantCulture)",
            source
        );
        Assert.Contains("internal static void __ActaTryResolveDbProjection<T>(ref Func<DbDataReader, T>? read)", source);
        Assert.Contains("if (typeof(T) == typeof(global::GenTests.EnqueueOutcomeRow))", source);
        Assert.Contains(
            "read = (Func<DbDataReader, T>)(object)global::Acta.Runtime.DbProjectionResolver.ByPosition<global::GenTests.EnqueueOutcomeRow>(EnqueueOutcomeRowColumns.Length, BindEnqueueOutcomeRow);",
            source
        );

        var resolver = SingleGeneratedSource(result, "TryResolveGenerated");
        Assert.Contains("namespace Acta.Runtime;", resolver);
        Assert.Contains("public static Func<DbDataReader, T> Resolve<T>()", resolver);
        Assert.Contains("global::GenTests.DbProjectionBinder.__ActaTryResolveDbProjection(ref read);", resolver);
    }

    [Fact]
    public void Code_member_decodes_through_FromId_and_a_plain_enum_casts()
    {
        var (result, compilation) = RunGenerator(
            """
            namespace GenTests;
            using System;
            using Acta.Runtime;

            [AttributeUsage(AttributeTargets.Enum)]
            internal sealed class CodeKindAttribute(string kind) : Attribute
            {
                public string Kind { get; } = kind;
                public bool Extensible { get; init; }
            }

            [CodeKind("open", Extensible = true)]
            internal enum OpenCode : byte
            {
                Unspecified = 0,
                Known = 10,
            }

            internal static class OpenCodeExtensions
            {
                public static OpenCode FromId(byte id) => id == 10 ? OpenCode.Known : OpenCode.Unspecified;
            }

            internal enum PlainCode : byte
            {
                Known = 10,
            }

            [DbProjection]
            internal sealed record CodedRow(OpenCode Open, OpenCode? MaybeOpen, PlainCode Plain);
            """
        );

        AssertNoCompileErrors(compilation);
        var source = SingleGeneratedSource(result, "BindCodedRow");
        Assert.Contains(
            "Open: global::GenTests.OpenCodeExtensions.FromId(Convert.ToByte(r.GetValue(o[0]), CultureInfo.InvariantCulture))",
            source
        );
        Assert.Contains(
            "MaybeOpen: r.IsDBNull(o[1]) ? null : global::GenTests.OpenCodeExtensions.FromId(Convert.ToByte(r.GetValue(o[1]), CultureInfo.InvariantCulture))",
            source
        );
        Assert.Contains("Plain: (global::GenTests.PlainCode)Convert.ToByte(r.GetValue(o[2]), CultureInfo.InvariantCulture)", source);
    }

    [Fact]
    public void A_type_the_code_resolves_binds_by_name_without_an_attribute()
    {
        var (result, compilation) = RunGenerator(
            """
            namespace GenTests;
            using Acta.Runtime;

            internal sealed record ExternalOutcome(int Id, string DisplayName);

            internal static class Caller
            {
                internal static object Read() => DbProjectionResolver.Resolve<ExternalOutcome>();
            }
            """
        );

        AssertNoCompileErrors(compilation);
        var source = SingleGeneratedSource(result, "BindExternalOutcome");
        Assert.Contains("internal static readonly string[] ExternalOutcomeColumns = [\"id\", \"display_name\"];", source);
    }

    [Fact]
    public void A_declared_projection_keeps_its_binding_when_the_code_also_resolves_it()
    {
        var (result, compilation) = RunGenerator(
            """
            namespace GenTests;
            using Acta.Runtime;

            [DbProjection]
            internal sealed record PositionalRow(int Id, string Name);

            internal static class Caller
            {
                internal static object Read() => DbProjectionResolver.Resolve<PositionalRow>();
            }
            """
        );

        AssertNoCompileErrors(compilation);
        var source = SingleGeneratedSource(result, "BindPositionalRow");
        Assert.Contains(
            "DbProjectionResolver.ByPosition<global::GenTests.PositionalRow>(PositionalRowColumns.Length, BindPositionalRow);",
            source
        );
        Assert.DoesNotContain("DbProjectionResolver.ByName<global::GenTests.PositionalRow>", source);
    }

    [Fact]
    public void Class_projection_emits_nullable_bytes_and_utc_reads()
    {
        var (result, compilation) = RunGenerator(
            """
            namespace GenTests;
            using System;
            using Acta.Runtime;

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
        Assert.Contains("Id: Convert.ToInt32(r.GetValue(o[0]), CultureInfo.InvariantCulture)", source);
        Assert.Contains("Name: r.IsDBNull(o[1]) ? null : r.GetString(o[1])", source);
        Assert.Contains("Payload: (byte[])r.GetValue(o[2])", source);
        Assert.Contains("CreatedAtUtc: r.GetDateTimeUtc(o[3])", source);
        Assert.Contains("AvailableAtUtc: r.IsDBNull(o[4]) ? null : r.GetDateTimeUtc(o[4])", source);
    }

    [Fact]
    public void Private_nested_projection_emits_binder_inside_containing_type()
    {
        var (result, compilation) = RunGenerator(
            """
            namespace GenTests;
            using Acta.Runtime;

            internal static partial class Operation
            {
                public static object Read(System.Data.Common.DbDataReader reader) => DbProjectionBinder.BindPrivateRow(reader, [0]);

                [DbProjection]
                private sealed record PrivateRow(int Id);
            }
            """
        );

        AssertNoCompileErrors(compilation);
        var source = SingleGeneratedSource(result, "BindPrivateRow");
        Assert.Contains("internal static partial class Operation", source);
        Assert.Contains("private static partial class DbProjectionBinder", source);
        Assert.Contains("public static global::GenTests.Operation.PrivateRow BindPrivateRow(DbDataReader r, int[] o)", source);
        Assert.Contains("internal static void __ActaTryResolveDbProjection<T>(ref Func<DbDataReader, T>? read)", source);
        Assert.Contains("if (typeof(T) == typeof(global::GenTests.Operation.PrivateRow))", source);
        Assert.Contains(
            "read = (Func<DbDataReader, T>)(object)global::Acta.Runtime.DbProjectionResolver.ByPosition<global::GenTests.Operation.PrivateRow>(DbProjectionBinder.PrivateRowColumns.Length, DbProjectionBinder.BindPrivateRow);",
            source
        );

        var resolver = SingleGeneratedSource(result, "TryResolveGenerated");
        Assert.Contains("global::GenTests.Operation.__ActaTryResolveDbProjection(ref read);", resolver);
    }

    [Fact]
    public void ByName_projection_reads_each_parameter_from_its_snake_case_column()
    {
        var (result, compilation) = RunGenerator(
            """
            namespace GenTests;
            using System;
            using Acta.Runtime;

            [DbProjection(ByName = true)]
            internal sealed record ListRow(long JobId, string JobNamespace, DateTime CreatedAtUtc);
            """
        );

        AssertNoCompileErrors(compilation);
        var source = SingleGeneratedSource(result, "BindListRow");
        Assert.Contains("internal static readonly string[] ListRowColumns = [\"job_id\", \"job_namespace\", \"created_at_utc\"];", source);
        Assert.Contains(
            "read = (Func<DbDataReader, T>)(object)global::Acta.Runtime.DbProjectionResolver.ByName<global::GenTests.ListRow>(\"ListRow\", ListRowColumns, BindListRow);",
            source
        );
    }

    [Fact]
    public void Ref_and_payload_members_build_from_their_columns()
    {
        var (result, compilation) = RunGenerator(
            """
            namespace Acta
            {
                public readonly record struct JobRef(System.Guid Value);

                public readonly struct JobPayloadFormat
                {
                    public static JobPayloadFormat ForId(byte id) => default;
                }

                public readonly struct JobPayload
                {
                    public static JobPayload FromBytes(JobPayloadFormat format, byte[] ownedBytes) => default;
                }
            }

            namespace GenTests
            {
                using Acta;
                using Acta.Runtime;

                [DbProjection(ByName = true)]
                internal sealed record DetailRow(JobRef JobRef, JobRef? ParentJobRef, JobPayload Input, JobPayload? Result);
            }
            """
        );

        AssertNoCompileErrors(compilation);
        var source = SingleGeneratedSource(result, "BindDetailRow");
        Assert.Contains(
            "DetailRowColumns = [\"job_ref\", \"parent_job_ref\", \"input_format_id\", \"input\", \"result_format_id\", \"result\"];",
            source
        );
        Assert.Contains("JobRef: new global::Acta.JobRef(r.GetGuid(o[0]))", source);
        Assert.Contains("ParentJobRef: r.IsDBNull(o[1]) ? null : new global::Acta.JobRef(r.GetGuid(o[1]))", source);
        Assert.Contains(
            "Input: global::Acta.JobPayload.FromBytes(global::Acta.JobPayloadFormat.ForId(Convert.ToByte(r.GetValue(o[2]), CultureInfo.InvariantCulture)), r.IsDBNull(o[3]) ? [] : (byte[])r.GetValue(o[3]))",
            source
        );
        Assert.Contains(
            "Result: r.IsDBNull(o[4]) || Convert.ToByte(r.GetValue(o[4]), CultureInfo.InvariantCulture) == 0 ? null : global::Acta.JobPayload.FromBytes(",
            source
        );
    }

    [Fact]
    public void Unsupported_projection_parameter_errors_ACTA0501()
    {
        var (result, _) = RunGenerator(
            """
            namespace GenTests;
            using System;
            using Acta.Runtime;

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
