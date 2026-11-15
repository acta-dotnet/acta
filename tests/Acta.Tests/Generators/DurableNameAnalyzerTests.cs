using Acta.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Acta.Tests.Generators;

/// <summary>
/// ACTA0152 reports a name a handler records that can never pass the name rule, a constant, an interpolation's own
/// text, or an unformatted number-typed hole opening a segment, and leaves alone every name some run-time value could
/// make valid.
/// </summary>
public sealed class DurableNameAnalyzerTests
{
    [Theory]
    [InlineData("await ctx.RunStepAsync($\"expire/{id}\", _ => Task.CompletedTask, ct);")]
    [InlineData("await ctx.RunStepAsync(\"charge_card\", _ => Task.CompletedTask, ct);")]
    [InlineData("await ctx.SetVariableAsync(\"stage..next\", 1, ct);")]
    [InlineData("await ctx.WaitSignalAsync(\"9-approval\", ct);")]
    [InlineData("await ctx.SleepAsync(\"cool-down-\", TimeSpan.FromMinutes(1), ct: ct);")]
    [InlineData("await ctx.SetVariableAsync(\"sys.progress\", 1, ct);")]
    [InlineData("await ctx.RunStepAsync($\"plan.1{id}\", _ => Task.CompletedTask, ct);")]
    [InlineData("await ctx.RunStepAsync($\"{id}.\", _ => Task.CompletedTask, ct);")]
    [InlineData("await ctx.RunStepAsync(\"\", _ => Task.CompletedTask, ct);")]
    [InlineData("await ctx.RunStepAsync($\"apply.{n}\", _ => Task.CompletedTask, ct);")]
    [InlineData("await ctx.RunStepAsync($\"{n}\", _ => Task.CompletedTask, ct);")]
    public async Task A_name_that_can_never_be_valid_is_reported(string body) => Assert.Single(await AnalyzeAsync(body));

    [Theory]
    [InlineData("await ctx.RunStepAsync(\"charge-card\", _ => Task.CompletedTask, ct);")]
    [InlineData("await ctx.RunStepAsync(\"Charge-Card\", _ => Task.CompletedTask, ct);")]
    [InlineData("await ctx.RunStepAsync($\"apply.{id}\", _ => Task.CompletedTask, ct);")]
    [InlineData("await ctx.RunStepAsync($\"plan.f{id}\", _ => Task.CompletedTask, ct);")]
    [InlineData("await ctx.RunStepAsync($\"invoice-{id}\", _ => Task.CompletedTask, ct);")]
    [InlineData("await ctx.RunStepAsync(name, _ => Task.CompletedTask, ct);")]
    [InlineData("await ctx.RunStepAsync($\"{id}\", _ => Task.CompletedTask, ct);")]
    [InlineData("await ctx.RunWithLockAsync(\"ledger:acme/1\", _ => Task.CompletedTask, ct: ct);")]
    [InlineData("await ctx.RunStepAsync($\"apply-{n}\", _ => Task.CompletedTask, ct);")]
    [InlineData("await ctx.RunStepAsync($\"batch.n{n}\", _ => Task.CompletedTask, ct);")]
    [InlineData("await ctx.RunStepAsync($\"shard.{n:x}\", _ => Task.CompletedTask, ct);")]
    public async Task A_name_some_value_could_make_valid_is_quiet(string body) => Assert.Empty(await AnalyzeAsync(body));

    [Fact]
    public async Task The_report_shows_a_letter_before_the_id()
    {
        var diagnostic = Assert.Single(await AnalyzeAsync("await ctx.RunStepAsync($\"apply.{n}\", _ => Task.CompletedTask, ct);"));
        Assert.Contains("($\"apply-{id}\")", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    private static async Task<Diagnostic[]> AnalyzeAsync(string body)
    {
        var source = $$"""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Acta;

            namespace AnalyzerTests;

            public static class Handler
            {
                public static async Task Run(JobContext ctx, string id, string name, int n, CancellationToken ct)
                {
                    {{body}}
                }
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Append(typeof(JobContext).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "AnalyzerTests",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var diagnostics = await compilation.WithAnalyzers([new DurableNameAnalyzer()]).GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics.Where(diagnostic => diagnostic.Id == "ACTA0152")];
    }
}
