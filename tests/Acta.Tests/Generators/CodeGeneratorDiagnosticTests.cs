using Acta.Generators.Codes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Acta.Tests.Generators;

/// <summary>
/// Drives <see cref="CodeGenerator"/> through Roslyn over small in-memory compilations and asserts
/// the code-family diagnostics it reports.
/// </summary>
public class CodeGeneratorDiagnosticTests
{
    private static GeneratorDriverRunResult RunGenerator(string source)
    {
        var referencePaths = new HashSet<string>(
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator),
            StringComparer.OrdinalIgnoreCase
        )
        {
            typeof(JobAttribute).Assembly.Location,
        };

        var compilation = CSharpCompilation.Create(
            "CodeGeneratorDiagnosticTests",
            [CSharpSyntaxTree.ParseText(source)],
            referencePaths.Select(p => MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        return CSharpGeneratorDriver.Create(new CodeGenerator()).RunGenerators(compilation).GetRunResult();
    }

    private static Diagnostic[] Of(GeneratorDriverRunResult result, string id) => [.. result.Diagnostics.Where(d => d.Id == id)];

    [Fact]
    public void Missing_CodeKind_errors_ACTA0201()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public enum SampleCode : byte
            {
                [Code("one", "First value.")]
                One = 1,
            }
            """
        );

        var error = Assert.Single(Of(result, "ACTA0201"));
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
    }

    [Fact]
    public void Non_kebab_CodeKind_errors_ACTA0201()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            [CodeKind("Sample Kind")]
            public enum SampleCode : byte
            {
                [Code("one", "First value.")]
                One = 1,
            }
            """
        );

        Assert.Single(Of(result, "ACTA0201"));
    }

    [Fact]
    public void Long_backed_enum_errors_ACTA0201()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            [CodeKind("sample")]
            public enum SampleCode : long
            {
                [Code("one", "First value.")]
                One = 1,
            }
            """
        );

        Assert.Single(Of(result, "ACTA0201"));
    }

    [Fact]
    public void Non_kebab_code_errors_ACTA0202()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            [CodeKind("sample")]
            public enum SampleCode : byte
            {
                [Code("Not Kebab", "First value.")]
                One = 1,
            }
            """
        );

        var error = Assert.Single(Of(result, "ACTA0202"));
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
    }

    [Fact]
    public void Non_byte_backed_family_errors_ACTA0201()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            [CodeKind("sample")]
            public enum SampleCode : int
            {
                [Code("huge", "Too big for short.")]
                Huge = 40000,
            }
            """
        );

        Assert.Single(Of(result, "ACTA0201"));
    }

    [Fact]
    public void Duplicate_code_string_errors_ACTA0203()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            [CodeKind("sample")]
            public enum SampleCode : byte
            {
                [Code("same", "First value.")]
                One = 1,

                [Code("same", "Second value.")]
                Two = 2,
            }
            """
        );

        var error = Assert.Single(Of(result, "ACTA0203"));
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
    }

    [Fact]
    public void Duplicate_numeric_value_errors_ACTA0203()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            [CodeKind("sample")]
            public enum SampleCode : byte
            {
                [Code("one", "First value.")]
                One = 1,

                [Code("alias", "Aliases the first value.")]
                Alias = 1,
            }
            """
        );

        Assert.Single(Of(result, "ACTA0203"));
    }

    [Fact]
    public void Valid_code_family_produces_no_diagnostics()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            [CodeKind("sample.kind")]
            public enum SampleCode : byte
            {
                [Code("one", "First value.")]
                One = 1,

                [Code("job.two", "Second value.")]
                Two = 2,
            }
            """
        );

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Closed_family_value_255_errors_ACTA0202()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            [CodeKind("sample")]
            public enum SampleCode : byte
            {
                [Code("invalid", "The closed-family sentinel is not assignable.")]
                Invalid = 255,
            }
            """
        );

        Assert.Single(Of(result, "ACTA0202"));
    }

    [Fact]
    public void Reserved_tombstone_identity_cannot_be_reused()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            [ReservedCode(42, "legacy")]
            [CodeKind("sample")]
            public enum SampleCode : byte
            {
                [Code("legacy", "Illegally reused tombstone.")]
                Reused = 42,
            }
            """
        );

        Assert.Single(Of(result, "ACTA0204"));
    }

    [Fact]
    public void Assignment_inside_reserved_range_errors_ACTA0204()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            [ReservedCodeRange(224, 254, "Architecture-controlled reserve")]
            [CodeKind("sample")]
            public enum SampleCode : byte
            {
                [Code("held", "Illegally assigned held id.")]
                Held = 224,
            }
            """
        );

        Assert.Single(Of(result, "ACTA0204"));
    }

    [Fact]
    public void Generated_conversions_read_canonical_strings_only()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            [ReservedCode(42, "legacy")]
            [ReservedCodeRange(224, 254, "Architecture-controlled reserve")]
            [CodeKind("sample")]
            public enum SampleCode : byte
            {
                [Code("one", "First value.")]
                One = 1,
            }
            """
        );

        Assert.Empty(result.Diagnostics);
        var generated = string.Concat(result.Results.Single().GeneratedSources.Select(s => s.SourceText.ToString()));
        Assert.Contains("public byte ToId => value switch", generated);
        Assert.Contains("public static SampleCode FromCode(string code)", generated);

        // The converter reads one token kind. Nothing decodes a JSON number or a numeric string, so
        // neither the Number case nor a TryParse fallback may reappear in the emitted Read.
        Assert.Contains("if (reader.TokenType != global::System.Text.Json.JsonTokenType.String)", generated);
        Assert.Contains("SampleCode expects a JSON string.", generated);
        Assert.DoesNotContain("JsonTokenType.Number", generated);
        Assert.DoesNotContain("byte.TryParse", generated);
    }

    [Fact]
    public void Reservation_only_family_is_discovered_and_generated()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            [ReservedCode(42, "legacy")]
            [CodeKind("retired-only")]
            public enum RetiredOnlyCode : byte
            {
            }
            """
        );

        Assert.Empty(result.Diagnostics);
        var generated = string.Concat(result.Results.Single().GeneratedSources.Select(s => s.SourceText.ToString()));
        Assert.Contains("public static partial class RetiredOnlyCodeExtensions", generated);
        Assert.Contains("public static bool IsKnownId(byte id) => false;", generated);
    }

    // Regression for a bug where `Lifecycle = CodeLifecycle.Deprecated` was read via
    // `named.Value.Value is int lc`, but CodeLifecycle is byte-backed, so Roslyn boxes the
    // TypedConstant as byte and the pattern never matched, silently emitting "Active" for every
    // family regardless of the declared lifecycle (F24).
    [Fact]
    public void Byte_backed_Lifecycle_argument_is_read_correctly()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            [CodeKind("sample")]
            public enum SampleCode : byte
            {
                [Code("one", "First value.")]
                One = 1,

                [Code("gone", "Superseded value.", Lifecycle = CodeLifecycle.Deprecated)]
                Gone = 2,
            }
            """
        );

        Assert.Empty(result.Diagnostics);
        var generated = string.Concat(result.Results.Single().GeneratedSources.Select(s => s.SourceText.ToString()));
        Assert.Contains("""new("sample", (byte)1, "one", "First value.", global::Acta.CodeLifecycle.Active),""", generated);
        Assert.Contains("""new("sample", (byte)2, "gone", "Superseded value.", global::Acta.CodeLifecycle.Deprecated),""", generated);
    }
}
