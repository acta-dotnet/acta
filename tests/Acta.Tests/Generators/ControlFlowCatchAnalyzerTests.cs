using System.Reflection;
using Acta.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Acta.Tests.Generators;

/// <summary>
/// ACTA0151 reports a catch that can take the control flow a wait, sleep, reschedule or pause throws, around a call that
/// can throw it, with a filter that can let it in, and nothing else.
/// </summary>
public sealed class ControlFlowCatchAnalyzerTests
{
    [Theory]
    [InlineData("try { await ctx.WaitSignalAsync(\"approved\", ct); } catch (Exception ex) { Log(ex); }")]
    [InlineData("try { await ctx.SleepAsync(\"nap\", TimeSpan.FromMinutes(1), ct: ct); } catch { }")]
    [InlineData("try { await ctx.RunStepAsync(\"charge\", _ => Task.CompletedTask, ct); } catch (ActaException) { }")]
    [InlineData("try { await ctx.RescheduleAsync(TimeSpan.FromMinutes(1), ct: ct); } catch (JobControlException) { }")]
    [InlineData("try { await ctx.PauseAsync(ct: ct); } catch (HandlerPauseException) { }")]
    [InlineData("try { await Approve(ctx, ct); } catch (Exception) { }")]
    [InlineData(
        "try { await ctx.RunStepAsync(\"charge\", _ => Task.CompletedTask, ct); } "
            + "catch (Exception ex) when (ctx.IsLastAttempt || ex is StepFailedException) "
            + "{ await ctx.RunStepAsync(\"release\", _ => Task.CompletedTask, ct); throw; }"
    )]
    [InlineData("try { await ctx.WaitSignalAsync(\"approved\", ct); } catch (Exception ex) when (ex is JobControlException) { }")]
    [InlineData(
        "try { await ctx.WaitSignalAsync(\"approved\", ct); } catch (Exception ex) when (ex is not JobControlException || ctx.IsLastAttempt) { }"
    )]
    public async Task A_catch_that_can_take_a_park_is_reported(string body) => Assert.Single(await AnalyzeAsync(body));

    [Theory]
    [InlineData(
        "try { await ctx.WaitSignalAsync(\"approved\", ct); } catch (Exception ex) when (ex is not JobControlException) { Log(ex); }"
    )]
    [InlineData(
        "try { await ctx.RunStepAsync(\"charge\", _ => Task.CompletedTask, ct); } catch (Exception ex) when (ex is StepFailedException) { }"
    )]
    [InlineData(
        "try { await ctx.RunStepAsync(\"charge\", _ => Task.CompletedTask, ct); } catch (Exception ex) when (ctx.EndsJob(ex)) "
            + "{ await ctx.RunStepAsync(\"release\", _ => Task.CompletedTask, ct); throw; }"
    )]
    [InlineData(
        "try { await ctx.WaitSignalAsync(\"approved\", ct); } catch (Exception ex) when (ex is not JobControlException && ctx.IsLastAttempt) { }"
    )]
    [InlineData("try { await ctx.RunStepAsync(\"charge\", _ => Task.CompletedTask, ct); } catch (StepFailedException) { }")]
    [InlineData("try { await ctx.FailAsync(\"bad input\", ct); } catch (HandlerFailException) { throw; }")]
    [InlineData("try { await ctx.SetVariableAsync(\"v\", 1, ct); } catch (Exception) { }")]
    [InlineData("try { await ctx.WaitSignalAsync(\"approved\", ct); } catch (Exception ex) { Log(ex); throw; }")]
    public async Task A_catch_that_cannot_take_a_park_or_only_observes_is_quiet(string body) => Assert.Empty(await AnalyzeAsync(body));

    [Fact]
    public async Task A_catch_around_a_pipeline_behaviors_next_is_reported()
    {
        const string behavior = """
            public sealed class Swallowing : IJobPipelineBehavior
            {
                public async ValueTask<JobHandlerOutput> InvokeAsync(object request, JobContext context, JobBehaviorDelegate next, CancellationToken ct)
                {
                    try { return await next(); } catch (Exception) { return new JobHandlerOutput(false, null); }
                }
            }
            """;

        Assert.Single(await AnalyzeAsync(body: "await Task.CompletedTask;", members: behavior));
    }

    [Fact]
    public void Every_public_JobContext_method_is_classified_as_one_that_can_park_or_one_that_cannot()
    {
        string[] cannotPark =
        [
            "AlertAsync",
            "CancelAsync",
            "DeleteVariableAsync",
            "EndsJob",
            "ExistsVariableAsync",
            "FailAsync",
            "GetChildResultAsync",
            "GetOrSetVariableAsync",
            "GetVariableAsync",
            "GetVariableOrDefaultAsync",
            "NoteAsync",
            "ResolveAlertAsync",
            "ResetStateAsync",
            "SetProgressAsync",
            "SetVariableAsync",
            "EnqueueChildAsync",
            "TryGetChildResultAsync",
        ];

        var unclassified = typeof(JobContext)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .Distinct()
            .Where(name => !ControlFlowCatchAnalyzer.ParkingMethods.Contains(name) && !cannotPark.Contains(name));

        Assert.Empty(unclassified);
    }

    private static async Task<Diagnostic[]> AnalyzeAsync(string body, string members = "")
    {
        var source = $$"""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Acta;

            namespace AnalyzerTests;

            public static class Handler
            {
                public static async Task Run(JobContext ctx, CancellationToken ct)
                {
                    {{body}}
                }

                private static Task Approve(JobContext ctx, CancellationToken ct) => Task.CompletedTask;

                private static void Log(Exception ex) { }
            }

            {{members}}
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

        var diagnostics = await compilation.WithAnalyzers([new ControlFlowCatchAnalyzer()]).GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics.Where(diagnostic => diagnostic.Id == "ACTA0151")];
    }
}
