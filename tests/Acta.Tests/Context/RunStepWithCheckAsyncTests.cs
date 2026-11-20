using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Acta.Tests.Context;

/// <summary>
/// The shapes a call to <c>RunStepWithCheckAsync</c> takes, compiled against Acta: which of the three forms each call
/// binds, which calls do not compile, and the argument checks.
/// </summary>
public sealed class RunStepWithCheckAsyncTests
{
    [Theory]
    [InlineData("t => api.ChargeAsync(t), t => api.FindChargeAsync(t), ct", "reference")]
    [InlineData("t => api.ChargeAsync(t), t => api.FindChargeAsync(t)", "reference")]
    [InlineData("t => api.ChargeAsync(t), _ => api.FindChargeAsync(), ct", "reference")]
    [InlineData("api.ChargeAsync, api.FindChargeAsync, ct", "reference")]
    [InlineData("t => api.CountAsync(t), t => api.FindCountAsync(t), ct", "value")]
    [InlineData("t => api.CountAsync(t), t => api.FindCountAsync(t)", "value")]
    [InlineData("t => api.SendAsync(t), t => api.WasSentAsync(t), ct", "no result")]
    [InlineData("t => api.SendAsync(t), t => api.WasSentAsync(t)", "no result")]
    [InlineData("t => api.ApproveAsync(t), t => api.WasApprovedAsync(t), ct", "value")]
    [InlineData("t => api.ApproveAsync(t), t => api.WasSentAsync(t), ct", "no result")]
    public void A_check_binds_the_form_of_its_step(string arguments, string form)
    {
        var (bound, diagnostics) = Compile($"await ctx.RunStepWithCheckAsync(\"charge-card\", {arguments});");

        Assert.Empty(diagnostics);
        Assert.Equal(form, bound);
    }

    [Theory]
    [InlineData("t => api.ChargeAsync(t), t => api.FindCountAsync(t), ct", "CS1662")]
    [InlineData("t => api.ChargeAsync(t), o => o.AtMostOnce(), ct", "CS1061")]
    [InlineData("t => api.MaybeCountAsync(t), t => api.FindCountAsync(t), ct", "CS1662")]
    public void A_check_that_does_not_fit_its_step_does_not_compile(string arguments, string error)
    {
        var (_, diagnostics) = Compile($"await ctx.RunStepWithCheckAsync(\"charge-card\", {arguments});");

        Assert.Contains(error, diagnostics);
    }

    // A bool body with a bool check binds the no-result form, so the result is dropped; a caller that reads it does not
    // compile, and a nullable check binds the value form.
    [Theory]
    [InlineData(
        "var approved = await ctx.RunStepWithCheckAsync(\"approve\", t => api.ApproveAsync(t), t => api.WasSentAsync(t), ct);",
        "CS0815"
    )]
    [InlineData(
        "bool approved = await ctx.RunStepWithCheckAsync(\"approve\", t => api.ApproveAsync(t), t => api.WasSentAsync(t), ct);",
        "CS0029"
    )]
    public void A_bool_result_read_from_a_bool_check_does_not_compile(string statement, string error) =>
        Assert.Contains(error, Compile(statement).Diagnostics);

    // A nullable reference body infers the type argument without its annotation and warns; naming it compiles clean.
    [Fact]
    public void A_nullable_reference_body_names_its_type_argument()
    {
        var inferred = Compile(
            "await ctx.RunStepWithCheckAsync(\"charge-card\", t => api.MaybeAsync(t), t => api.FindChargeAsync(t), ct);"
        );
        var named = Compile(
            "await ctx.RunStepWithCheckAsync<string?>(\"charge-card\", t => api.MaybeAsync(t), t => api.FindChargeAsync(t), ct);"
        );

        Assert.Equal("reference", inferred.Form);
        Assert.Equal(["CS8619"], inferred.Diagnostics);
        Assert.Equal("reference", named.Form);
        Assert.Empty(named.Diagnostics);
    }

    [Fact]
    public async Task A_null_body_or_check_is_refused_in_every_form()
    {
        var ctx = new RecordingJobContext();
        var ct = TestContext.Current.CancellationToken;

        var refused = new[]
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                ctx.RunStepWithCheckAsync("charge", null!, _ => Task.FromResult<string?>(null), ct)
            ),
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                ctx.RunStepWithCheckAsync("charge", _ => Task.FromResult("ch_1"), (Func<CancellationToken, Task<string?>>)null!, ct)
            ),
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                ctx.RunStepWithCheckAsync("count", null!, _ => Task.FromResult<int?>(null), ct)
            ),
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                ctx.RunStepWithCheckAsync("count", _ => Task.FromResult(1), (Func<CancellationToken, Task<int?>>)null!, ct)
            ),
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                ctx.RunStepWithCheckAsync("refund", null!, _ => Task.FromResult(false), ct)
            ),
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                ctx.RunStepWithCheckAsync("refund", _ => Task.CompletedTask, (Func<CancellationToken, Task<bool>>)null!, ct)
            ),
        };

        Assert.Equal(["body", "check", "body", "check", "body", "check"], refused.Select(ex => ex.ParamName));
        Assert.Empty(ctx.Steps);
    }

    private static (string? Form, string[] Diagnostics) Compile(string statement)
    {
        var source = $$"""
            #nullable enable
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Acta;

            namespace ShapeTests;

            public sealed class Api
            {
                public Task<string> ChargeAsync(CancellationToken ct) => Task.FromResult("ch_1");
                public Task<string?> FindChargeAsync(CancellationToken ct) => Task.FromResult<string?>(null);
                public Task<string?> FindChargeAsync() => Task.FromResult<string?>(null);
                public Task<string?> MaybeAsync(CancellationToken ct) => Task.FromResult<string?>(null);
                public Task<int> CountAsync(CancellationToken ct) => Task.FromResult(1);
                public Task<int?> FindCountAsync(CancellationToken ct) => Task.FromResult<int?>(null);
                public Task<int?> MaybeCountAsync(CancellationToken ct) => Task.FromResult<int?>(null);
                public Task SendAsync(CancellationToken ct) => Task.CompletedTask;
                public Task<bool> ApproveAsync(CancellationToken ct) => Task.FromResult(true);
                public Task<bool> WasSentAsync(CancellationToken ct) => Task.FromResult(false);
                public Task<bool?> WasApprovedAsync(CancellationToken ct) => Task.FromResult<bool?>(null);
            }

            public static class Handler
            {
                public static async Task Run(JobContext ctx, Api api, CancellationToken ct)
                {
                    {{statement}}
                }
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Append(typeof(JobContext).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path));
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "ShapeTests",
            [tree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
        );
        var call = tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(i => i.ToString().Contains("RunStepWithCheckAsync", StringComparison.Ordinal));
        var method = compilation.GetSemanticModel(tree).GetSymbolInfo(call).Symbol as IMethodSymbol;
        var form = method?.OriginalDefinition switch
        {
            null => null,
            { IsGenericMethod: false } => "no result",
            { TypeParameters: [{ HasValueTypeConstraint: true }] } => "value",
            _ => "reference",
        };
        // Errors, and the nullable warnings (CS86xx) a caller's build would show for the call.
        var diagnostics = compilation
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error || d.Id.StartsWith("CS86", StringComparison.Ordinal))
            .Select(d => d.Id)
            .Distinct()
            .ToArray();
        return (form, diagnostics);
    }
}
