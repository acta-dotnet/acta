using Acta;

namespace TestJobs;

/// <summary>
/// <c>DoNotRetry</c> probes: each throws an exception outside Acta's built-in list with attempts to spare, so only a host's
/// declaration ends the job at once. The one-shot probes compensate under <c>ctx.EndsJob</c> and record it as the
/// <c>compensated</c> variable.
/// </summary>
public static class DoNotRetryProbes
{
    [Job("do-not-retry-argument", MaxAttempts = 3)]
    public static Task Argument(JobContext ctx, CancellationToken ct) =>
        CompensatedAsync(ctx, new ArgumentException("The order id is malformed."), ct);

    [Job("do-not-retry-argument-null", MaxAttempts = 3)]
    public static Task ArgumentNull(JobContext ctx, CancellationToken ct) =>
        CompensatedAsync(ctx, new ArgumentNullException("orderId"), ct);

    [Job("do-not-retry-scheduled", MaxAttempts = 3)]
    [Schedule("default", Cron.Daily)]
    public static Task Scheduled(JobContext ctx, CancellationToken ct) => throw new ArgumentException("The feed is malformed.");

    private static async Task CompensatedAsync(JobContext ctx, Exception thrown, CancellationToken ct)
    {
        try
        {
            await Task.Yield();
            throw thrown;
        }
        catch (Exception ex) when (ctx.EndsJob(ex))
        {
            await ctx.SetVariableAsync("compensated", true, ct);
            throw;
        }
    }
}
