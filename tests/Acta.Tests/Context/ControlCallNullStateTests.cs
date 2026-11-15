using Xunit;

namespace Acta.Tests.Context;

/// <summary>
/// <c>FailAsync</c> and <c>CancelAsync</c> never return, and the compiler knows it: a null check that ends in one narrows
/// the value after it. The helpers below compile only while that holds, since warnings are errors.
/// </summary>
public sealed class ControlCallNullStateTests
{
    [Fact]
    public async Task A_null_check_that_fails_the_job_narrows_the_value_after_it()
    {
        var ctx = new RecordingJobContext();

        Assert.Equal(5, await LengthOrFailAsync(ctx, "label"));
        await Assert.ThrowsAsync<HandlerFailException>(() => LengthOrFailAsync(ctx, null));
    }

    [Fact]
    public async Task A_null_check_that_cancels_the_job_narrows_the_value_after_it()
    {
        var ctx = new RecordingJobContext();

        Assert.Equal(5, await LengthOrCancelAsync(ctx, "label"));
        await Assert.ThrowsAsync<HandlerCancelException>(() => LengthOrCancelAsync(ctx, null));
    }

    private static async Task<int> LengthOrFailAsync(JobContext ctx, string? name)
    {
        if (name is null)
        {
            await ctx.FailAsync("no name");
        }

        return name.Length;
    }

    private static async Task<int> LengthOrCancelAsync(JobContext ctx, string? name)
    {
        if (name is null)
        {
            await ctx.CancelAsync("no name");
        }

        return name.Length;
    }
}
