using Xunit;

namespace Acta.Tests.Context;

/// <summary>
/// Every call that can suspend the job is refused inside an at-most-once or check step body, at the call and before it
/// writes anything: the resume would replay the body, or ask the check and skip the rest of it. Outside one, the same
/// calls run.
/// </summary>
public sealed class AtMostOnceSuspensionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

    private static readonly EnqueueJobResponse Child = new(7, JobRef.New(), EnqueueJobAction.Inserted);

    public static TheoryData<string> Calls => [.. SuspendingCalls.Keys];

    private static readonly Dictionary<string, Func<JobContext, CancellationToken, Task>> SuspendingCalls = new()
    {
        ["SleepAsync"] = (ctx, ct) => ctx.SleepAsync("nap", TimeSpan.FromMinutes(1), ct: ct),
        ["SleepUntilAsync"] = (ctx, ct) => ctx.SleepUntilAsync("nap", DateTimeOffset.UtcNow.AddMinutes(1), ct: ct),
        ["WaitSignalAsync"] = (ctx, ct) => ctx.WaitSignalAsync("approval", ct),
        ["WaitSignalAsync<T>"] = (ctx, ct) => ctx.WaitSignalAsync<string>("approval", ct),
        ["WaitSignalAsync timeout"] = (ctx, ct) => ctx.WaitSignalAsync("approval", Timeout, ct),
        ["WaitSignalAsync<T> timeout"] = (ctx, ct) => ctx.WaitSignalAsync<string>("approval", Timeout, ct),
        ["WaitChildAsync"] = (ctx, ct) => ctx.WaitChildAsync(Child, ct),
        ["WaitChildAsync timeout"] = (ctx, ct) => ctx.WaitChildAsync(Child, Timeout, ct),
        ["CallChildAsync"] = (ctx, ct) => ctx.CallChildAsync("ship", new NoInput(), ct),
        ["CallChildAsync timeout"] = (ctx, ct) => ctx.CallChildAsync("ship", new NoInput(), Timeout, ct),
        ["WaitChildrenAsync"] = (ctx, ct) => ctx.WaitChildrenAsync([], ct),
        ["WaitChildrenAsync timeout"] = (ctx, ct) => ctx.WaitChildrenAsync([], Timeout, ct),
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

    public static TheoryData<string, string> CallsInEachCheckForm
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var call in SuspendingCalls.Keys)
            {
                foreach (var form in CheckForms.Keys)
                {
                    data.Add(call, form);
                }
            }
            return data;
        }
    }

    // The three forms of RunStepWithCheckAsync around the same body, with a check that finds nothing.
    private static readonly Dictionary<string, Func<JobContext, Func<CancellationToken, Task>, CancellationToken, Task>> CheckForms = new()
    {
        ["reference"] = (ctx, body, ct) =>
            ctx.RunStepWithCheckAsync(
                "charge",
                async token =>
                {
                    await body(token);
                    return "ch_1";
                },
                _ => Task.FromResult<string?>(null),
                ct
            ),
        ["value"] = (ctx, body, ct) =>
            ctx.RunStepWithCheckAsync(
                "charge",
                async token =>
                {
                    await body(token);
                    return 1;
                },
                _ => Task.FromResult<int?>(null),
                ct
            ),
        ["no result"] = (ctx, body, ct) => ctx.RunStepWithCheckAsync("charge", body, _ => Task.FromResult(false), ct),
    };

    // The refusal is a StepFailedException, which no retry runs again: a retry would ask the check, find the body's
    // effect, and skip the rest of the body.
    [Theory]
    [MemberData(nameof(CallsInEachCheckForm))]
    public async Task A_suspending_call_inside_a_check_step_body_fails_the_step_for_good_before_it_writes(string call, string form)
    {
        var ctx = new RecordingJobContext();
        var ct = TestContext.Current.CancellationToken;

        var refused = await Assert.ThrowsAsync<StepFailedException>(() =>
            CheckForms[form](ctx, token => SuspendingCalls[call](ctx, token), ct)
        );

        Assert.Equal("charge", refused.StepName);
        Assert.Contains("inside a check step's body is refused", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Move the wait between steps.", refused.Message, StringComparison.Ordinal);
        Assert.Empty(ctx.Events);
    }

    [Fact]
    public async Task The_mark_ends_with_the_check_step()
    {
        var ctx = new RecordingJobContext();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(
            "ch_1",
            await ctx.RunStepWithCheckAsync("charge", _ => Task.FromResult("ch_1"), _ => Task.FromResult<string?>(null), ct)
        );
        Assert.Equal(3, await ctx.RunStepWithCheckAsync("count", _ => Task.FromResult(3), _ => Task.FromResult<int?>(null), ct));
        await ctx.RunStepWithCheckAsync("refund", _ => Task.CompletedTask, _ => Task.FromResult(false), ct);

        // A wait after a check step runs, and so does one inside an ordinary step.
        await ctx.WaitSignalAsync("approval", ct);
        await ctx.RunStepAsync("approve", token => ctx.WaitSignalAsync("approval", token), ct);
        Assert.Equal(["wait:approval", "wait:approval"], ctx.Events);
        Assert.Equal([true, true, true, false], ctx.Steps.Select(step => step.Checked));
    }
}
