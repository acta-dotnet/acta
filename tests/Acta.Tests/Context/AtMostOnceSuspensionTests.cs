using Xunit;

namespace Acta.Tests.Context;

/// <summary>
/// Every call that can suspend the job is refused inside an at-most-once step body, at the call and before it writes
/// anything: the resume would replay the body. Outside one, the same calls run.
/// </summary>
public sealed class AtMostOnceSuspensionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

    public static TheoryData<string> Calls => [.. SuspendingCalls.Keys];

    private static readonly Dictionary<string, Func<JobContext, CancellationToken, Task>> SuspendingCalls = new()
    {
        ["SleepAsync"] = (ctx, ct) => ctx.SleepAsync("nap", TimeSpan.FromMinutes(1), ct: ct),
        ["SleepUntilAsync"] = (ctx, ct) => ctx.SleepUntilAsync("nap", DateTimeOffset.UtcNow.AddMinutes(1), ct: ct),
        ["WaitSignalAsync"] = (ctx, ct) => ctx.WaitSignalAsync("approval", ct),
        ["WaitSignalAsync<T>"] = (ctx, ct) => ctx.WaitSignalAsync<string>("approval", ct),
        ["WaitSignalAsync timeout"] = (ctx, ct) => ctx.WaitSignalAsync("approval", Timeout, ct),
        ["WaitSignalAsync<T> timeout"] = (ctx, ct) => ctx.WaitSignalAsync<string>("approval", Timeout, ct),
        ["TryWaitSignalAsync"] = (ctx, ct) => ctx.TryWaitSignalAsync("approval", Timeout, ct),
        ["TryWaitSignalAsync<T>"] = (ctx, ct) => ctx.TryWaitSignalAsync<string>("approval", Timeout, ct),
        ["WaitChildAsync"] = (ctx, ct) => ctx.WaitChildAsync(7, ct),
        ["TryWaitChildAsync"] = (ctx, ct) => ctx.TryWaitChildAsync(7, Timeout, ct),
        ["WaitChildrenAsync"] = (ctx, ct) => ctx.WaitChildrenAsync([7], ct),
        ["TryWaitChildrenAsync"] = (ctx, ct) => ctx.TryWaitChildrenAsync([7], Timeout, ct),
        ["ExecuteChildAsync"] = (ctx, ct) => ctx.ExecuteChildAsync("ship", new NoInput(), ct),
        ["ExecuteChildAsync timeout"] = (ctx, ct) => ctx.ExecuteChildAsync("ship", new NoInput(), Timeout, ct),
        ["ExecuteChildAsync<T>"] = (ctx, ct) => ctx.ExecuteChildAsync<NoInput, string>("ship", new NoInput(), ct),
        ["ExecuteChildAsync<T> timeout"] = (ctx, ct) => ctx.ExecuteChildAsync<NoInput, string>("ship", new NoInput(), Timeout, ct),
        ["JoinAsync"] = (ctx, ct) => ctx.JoinAsync([], ct),
        ["JoinAsync timeout"] = (ctx, ct) => ctx.JoinAsync([], Timeout, ct),
        ["ParallelAsync"] = (ctx, ct) => ctx.ParallelAsync("ship", b => b.Child("a", new NoInput()), ct),
        ["ParallelAsync timeout"] = (ctx, ct) => ctx.ParallelAsync("ship", b => b.Child("a", new NoInput()), Timeout, ct),
        ["MapAsync"] = (ctx, ct) => ctx.MapAsync("ship", new[] { 1 }, i => i, _ => new NoInput(), ct),
        ["MapAsync timeout"] = (ctx, ct) => ctx.MapAsync("ship", new[] { 1 }, i => i, _ => new NoInput(), Timeout, ct),
        ["RescheduleAsync"] = (ctx, ct) => ctx.RescheduleAsync(TimeSpan.FromMinutes(1), ct: ct),
        ["RescheduleUntilAsync"] = (ctx, ct) => ctx.RescheduleUntilAsync(DateTimeOffset.UtcNow.AddMinutes(1), ct: ct),
        ["PauseAsync"] = (ctx, ct) => ctx.PauseAsync(ct: ct),
    };

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task A_suspending_call_inside_an_at_most_once_body_is_refused_before_it_writes(string call)
    {
        var ctx = new RecordingJobContext();
        var ct = TestContext.Current.CancellationToken;

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ctx.RunStepAsync("charge", token => SuspendingCalls[call](ctx, token), o => o.AtMostOnce(), ct)
        );

        Assert.Contains("AtMostOnce step 'charge'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Move the wait out of the step.", refused.Message, StringComparison.Ordinal);
        Assert.Empty(ctx.Events);
    }

    [Fact]
    public async Task A_nested_ordinary_step_inherits_the_refusal_and_the_mark_ends_with_the_at_most_once_step()
    {
        var ctx = new RecordingJobContext();
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ctx.RunStepAsync(
                "charge",
                token => ctx.RunStepAsync("inner", inner => ctx.WaitSignalAsync("approval", inner), token),
                o => o.AtMostOnce(),
                ct
            )
        );
        await ctx.RunStepAsync("charge-again", _ => Task.CompletedTask, o => o.AtMostOnce(), ct);

        // Outside an at-most-once body the same waits run: after one, and inside an ordinary step.
        await ctx.WaitSignalAsync("approval", ct);
        await ctx.RunStepAsync("approve", token => ctx.WaitSignalAsync("approval", token), ct);
        Assert.Equal(["wait:approval", "wait:approval"], ctx.Events);
    }
}
