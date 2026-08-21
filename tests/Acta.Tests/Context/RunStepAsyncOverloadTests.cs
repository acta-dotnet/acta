using Xunit;

namespace Acta.Tests.Context;

/// <summary>
/// Overload-resolution and forwarding tests for the positional-<c>ct</c> convenience overloads of
/// <c>RunStepAsync</c>: a bare <c>ct</c> in the third slot must resolve to the new overload (not an
/// implicit-conversion surprise on the canonical <c>configure</c>-taking one) and behave identically
/// to the named <c>configure: null, ct: ct</c> form.
/// </summary>
public sealed class RunStepAsyncOverloadTests
{
    [Fact]
    public async Task Void_positional_ct_overload_forwards_configure_null_like_the_named_form()
    {
        var viaPositional = new RecordingJobContext();
        var viaNamed = new RecordingJobContext();

        await viaPositional.RunStepAsync("reserve-stock", _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await viaNamed.RunStepAsync("reserve-stock", _ => Task.CompletedTask, configure: null, ct: TestContext.Current.CancellationToken);

        var positional = Assert.Single(viaPositional.Steps);
        var named = Assert.Single(viaNamed.Steps);
        Assert.Equal("reserve-stock", positional.Name);
        Assert.Equal(StepOptions.Inherit, positional.Options);
        Assert.Equal(named.Options, positional.Options);
    }

    [Fact]
    public async Task Result_positional_ct_overload_forwards_configure_null_like_the_named_form()
    {
        var viaPositional = new RecordingJobContext();
        var viaNamed = new RecordingJobContext();

        var positionalResult = await viaPositional.RunStepAsync(
            "charge-card",
            _ => Task.FromResult(42),
            TestContext.Current.CancellationToken
        );
        var namedResult = await viaNamed.RunStepAsync(
            "charge-card",
            _ => Task.FromResult(42),
            configure: null,
            ct: TestContext.Current.CancellationToken
        );

        Assert.Equal(42, positionalResult);
        Assert.Equal(namedResult, positionalResult);
        var positional = Assert.Single(viaPositional.Steps);
        Assert.Equal(StepOptions.Inherit, positional.Options);
    }

    [Fact]
    public async Task Void_overload_resolves_for_a_lambda_literal_without_ambiguity()
    {
        var ctx = new RecordingJobContext();

        await ctx.RunStepAsync(
            "ship",
            async c =>
            {
                await Task.Yield();
                c.ThrowIfCancellationRequested();
            },
            TestContext.Current.CancellationToken
        );

        Assert.Single(ctx.Steps);
    }

    [Fact]
    public async Task Result_overload_resolves_for_a_value_returning_lambda_literal_without_ambiguity()
    {
        var ctx = new RecordingJobContext();

        var result = await ctx.RunStepAsync(
            "reserve-stock",
            async c =>
            {
                await Task.Yield();
                return c.IsCancellationRequested ? -1 : 7;
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(7, result);
    }
}
