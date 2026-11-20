using System.Collections.Concurrent;
using System.Globalization;
using Acta;

namespace TestJobs;

/// <summary>
/// What goes wrong in the check-step charge besides a crash a spec stages. Its body throws once after its effect, for a
/// retry (<c>BodyThrows</c>) or for good (<c>BodyFailsForGood</c>); its check throws once; the handler resets its state
/// at the top of every attempt; or the handler catches the body's throw and runs the step again in the same attempt.
/// </summary>
public sealed record CheckStepFault(
    bool BodyThrows = false,
    bool BodyFailsForGood = false,
    bool CheckThrows = false,
    bool ResetFirst = false,
    bool RunAgainInHandler = false
);

/// <summary>
/// A check step whose body waits, in one of the method's three forms (<c>reference</c>, <c>value</c>, or <c>no result</c>),
/// with the wait after the body's effect or before it.
/// </summary>
public sealed record CheckStepWait(string Form, bool EffectFirst);

/// <summary>
/// Check-step probe handlers around a provider that remembers its effects. A body applies its effect to <see cref="Effects"/>
/// and a check reads it back; both count their invocations per job id, so a spec proves which of them ran on each
/// attempt. A spec stages a crash between the effect and its record by starting the step and setting the effect itself.
/// After its step the charge, the count and the refund keep what the step returned and wait on the signal "proceed", so
/// the execution after the raise replays the step.
/// </summary>
public static class JobCheckStepProbes
{
    public static readonly ConcurrentDictionary<long, int> BodyInvocations = new();

    public static readonly ConcurrentDictionary<long, int> CheckInvocations = new();

    /// <summary>The effect the provider holds per job id: what a body applied, or what a spec staged as applied.</summary>
    public static readonly ConcurrentDictionary<long, string> Effects = new();

    /// <summary>What the step returned to the handler on the job's latest pass through it, per job id.</summary>
    public static readonly ConcurrentDictionary<long, object> Returned = new();

    /// <summary>What a wait inside a check step's body threw at the call, per job id.</summary>
    public static readonly ConcurrentDictionary<long, Exception> RefusedWaits = new();

    private static readonly ConcurrentDictionary<long, (TaskCompletionSource Entered, TaskCompletionSource Release)> CheckHolds = new();

    public const string BodyReceipt = "receipt-from-body";

    /// <summary>Holds the job's next check before it answers, until the spec releases it.</summary>
    public static (Task Entered, Action Release) HoldNextCheck(long jobId)
    {
        var hold = (
            Entered: new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            Release: new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        );
        CheckHolds[jobId] = hold;
        return (hold.Entered.Task, () => hold.Release.TrySetResult());
    }

    private static int Count(ConcurrentDictionary<long, int> counts, long jobId) => counts.AddOrUpdate(jobId, 1, static (_, n) => n + 1);

    // The provider's lookup: counts the call, fails the job's first one when asked to, and answers what it holds once a
    // held check is released.
    private static async Task<string?> LookUpAsync(long jobId, CancellationToken ct, bool failFirst = false)
    {
        if (Count(CheckInvocations, jobId) == 1 && failFirst)
        {
            throw new TimeoutException("The provider did not answer the lookup.");
        }

        if (CheckHolds.TryRemove(jobId, out var hold))
        {
            hold.Entered.TrySetResult();
            await hold.Release.Task.WaitAsync(ct);
        }
        return Effects.TryGetValue(jobId, out var found) ? found : null;
    }

    // A charge with a reference-type result: the check answers the receipt the provider holds, or null.
    [Job("job-check-step", MaxAttempts = 3, Backoff = "0s")]
    public static async Task<string> CheckStep(CheckStepFault input, JobContext ctx, CancellationToken ct)
    {
        if (input.ResetFirst)
        {
            await ctx.ResetStateAsync(ct);
        }

        string receipt;
        try
        {
            receipt = await ChargeAsync(input, ctx, ct);
        }
        catch (TimeoutException) when (input.RunAgainInHandler)
        {
            receipt = await ChargeAsync(input, ctx, ct);
        }

        Returned[ctx.JobId] = receipt;
        await ctx.WaitSignalAsync("proceed", ct);
        return receipt;
    }

    private static Task<string> ChargeAsync(CheckStepFault input, JobContext ctx, CancellationToken ct) =>
        ctx.RunStepWithCheckAsync(
            "charge",
            _ =>
            {
                var n = Count(BodyInvocations, ctx.JobId);
                Effects[ctx.JobId] = BodyReceipt;
                if (n == 1 && input.BodyFailsForGood)
                {
                    throw new NotSupportedException("The provider refused the charge's reply.");
                }
                if (n == 1 && (input.BodyThrows || input.RunAgainInHandler))
                {
                    throw new TimeoutException("The reply was lost after the charge applied.");
                }
                return Task.FromResult(BodyReceipt);
            },
            token => LookUpAsync(ctx.JobId, token, failFirst: input.CheckThrows),
            ct
        );

    // A count with a value-type result: the check answers the count the provider holds, or null.
    [Job("job-check-step-value")]
    public static async Task<int> CheckStepValue(JobContext ctx, CancellationToken ct)
    {
        var count = await ctx.RunStepWithCheckAsync(
            "count",
            _ =>
            {
                Count(BodyInvocations, ctx.JobId);
                Effects[ctx.JobId] = "7";
                return Task.FromResult(7);
            },
            async token => await LookUpAsync(ctx.JobId, token) is { } found ? int.Parse(found, CultureInfo.InvariantCulture) : (int?)null,
            ct
        );
        Returned[ctx.JobId] = count;
        await ctx.WaitSignalAsync("proceed", ct);
        return count;
    }

    // A refund with no result: the check answers whether the provider holds it.
    [Job("job-check-step-void")]
    public static async Task CheckStepVoid(JobContext ctx, CancellationToken ct)
    {
        await ctx.RunStepWithCheckAsync(
            "refund",
            _ =>
            {
                Count(BodyInvocations, ctx.JobId);
                Effects[ctx.JobId] = "refunded";
                return Task.CompletedTask;
            },
            async token => await LookUpAsync(ctx.JobId, token) is not null,
            ct
        );
        await ctx.WaitSignalAsync("proceed", ct);
    }

    // A wait inside a check step's body, after the body's effect or before it, in the form the input names. The job has
    // attempts left, so only a refusal that ends the job at once keeps it from a retry.
    [Job("job-check-step-wait", MaxAttempts = 3, Backoff = "0s")]
    public static async Task WaitInsideCheckStep(CheckStepWait input, JobContext ctx, CancellationToken ct)
    {
        switch (input.Form)
        {
            case "reference":
                await ctx.RunStepWithCheckAsync(
                    "pack",
                    async token =>
                    {
                        await PackAsync(input, ctx, token);
                        return BodyReceipt;
                    },
                    token => LookUpAsync(ctx.JobId, token),
                    ct
                );
                break;
            case "value":
                await ctx.RunStepWithCheckAsync(
                    "pack",
                    async token =>
                    {
                        await PackAsync(input, ctx, token);
                        return 7;
                    },
                    async token => await LookUpAsync(ctx.JobId, token) is null ? null : (int?)7,
                    ct
                );
                break;
            default:
                await ctx.RunStepWithCheckAsync(
                    "pack",
                    token => PackAsync(input, ctx, token),
                    async token => await LookUpAsync(ctx.JobId, token) is not null,
                    ct
                );
                break;
        }
    }

    private static async Task PackAsync(CheckStepWait input, JobContext ctx, CancellationToken ct)
    {
        Count(BodyInvocations, ctx.JobId);
        if (input.EffectFirst)
        {
            Effects[ctx.JobId] = BodyReceipt;
        }

        try
        {
            await ctx.WaitSignalAsync("packed", ct);
        }
        catch (Exception ex)
        {
            RefusedWaits[ctx.JobId] = ex;
            throw;
        }

        Effects[ctx.JobId] = BodyReceipt;
    }

    // A check that finds a value which cannot be serialized.
    [Job("job-check-step-unrecorded")]
    public static async Task CheckStepUnrecorded(JobContext ctx, CancellationToken ct) =>
        await ctx.RunStepWithCheckAsync(
            "charge",
            _ =>
            {
                Count(BodyInvocations, ctx.JobId);
                return Task.FromResult(new UnserializableOutput(ctx.JobId));
            },
            _ =>
            {
                Count(CheckInvocations, ctx.JobId);
                return Task.FromResult<UnserializableOutput?>(new UnserializableOutput(ctx.JobId));
            },
            ct
        );

    // The charge as an at-most-once step, which takes no check.
    [Job("job-check-step-at-most-once")]
    public static Task<string> CheckStepAtMostOnce(JobContext ctx, CancellationToken ct) =>
        ctx.RunStepAsync(
            "charge",
            _ =>
            {
                Count(BodyInvocations, ctx.JobId);
                return Task.FromResult(BodyReceipt);
            },
            o => o.AtMostOnce(),
            ct
        );
}
