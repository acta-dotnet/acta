using Acta;

namespace TestJobs;

/// <summary>Outcome of the sleep-argument validation probe.</summary>
public sealed record SleepValidationResult(bool InvalidNameRejected, bool ReservedNameRejected, bool NegativeDelayRejected);

/// <summary>
/// Reschedule + durable-sleep probe handlers. Handlers restart from the top on every attempt, so each
/// writes its observable variables on the path it actually reaches.
/// </summary>
public static class JobSleepRescheduleProbes
{
    [Job("job-reschedule-delay")]
    public static async Task RescheduleDelay(JobContext ctx, CancellationToken ct)
    {
        await ctx.SetVariableAsync("ran.before", true, ct);
        await ctx.RescheduleAsync(TimeSpan.FromMinutes(10), "cooldown", ct);
        await ctx.SetVariableAsync("ran.after", true, ct);
    }

    [Job("job-reschedule-until-past")]
    public static Task RescheduleUntilPast(JobContext ctx, CancellationToken ct) =>
        ctx.RescheduleUntilAsync(DateTimeOffset.UtcNow.AddMinutes(-5), "immediate", ct);

    [Job("job-sleep-basic")]
    public static async Task SleepBasic(JobContext ctx, CancellationToken ct)
    {
        await ctx.SetVariableAsync("ran.before", true, ct);
        await ctx.SleepAsync("nap", TimeSpan.FromMinutes(10), "waiting", ct);
        await ctx.SetVariableAsync("ran.after", true, ct);
    }

    [Job("job-sleep-then-reschedule")]
    public static async Task SleepThenReschedule(JobContext ctx, CancellationToken ct)
    {
        await ctx.SleepAsync("nap", TimeSpan.FromMinutes(10), "waiting", ct);
        await ctx.RescheduleAsync(TimeSpan.FromMinutes(10), "cooldown", ct);
    }

    [Job("job-sleep-zero")]
    public static async Task SleepZero(JobContext ctx, CancellationToken ct)
    {
        await ctx.SleepAsync("instant", TimeSpan.Zero, ct: ct);
        await ctx.SetVariableAsync("ran.after", true, ct);
    }

    [Job("job-sleep-reject")]
    public static async Task SleepReject(JobContext ctx, CancellationToken ct)
    {
        await ctx.SleepAsync("second-name", TimeSpan.FromMinutes(10), ct: ct);
        await ctx.SetVariableAsync("ran.after", true, ct);
    }

    [Job("job-sleep-validation")]
    public static async Task<SleepValidationResult> SleepValidation(JobContext ctx, CancellationToken ct)
    {
        var invalid = await Rejects<ArgumentException>(() => ctx.SleepAsync("Bad Name", TimeSpan.FromMinutes(1), ct: ct));
        var reserved = await Rejects<ArgumentException>(() => ctx.SleepAsync("sys.reserved", TimeSpan.FromMinutes(1), ct: ct));
        var negative = await Rejects<ArgumentOutOfRangeException>(() => ctx.SleepAsync("neg", TimeSpan.FromSeconds(-1), ct: ct));
        return new SleepValidationResult(invalid, reserved, negative);
    }

    private static async Task<bool> Rejects<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }
}
