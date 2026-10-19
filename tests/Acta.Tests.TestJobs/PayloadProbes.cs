using Acta;

namespace TestJobs;

/// <summary>
/// Handlers exercising <c>MaxInlinePayloadBytes</c> enforcement inside a running handler: an oversize
/// variable write must throw <c>PayloadTooLargeException</c> (caller-controlled), while an oversize
/// handler result is dropped with a warning, and the job still completes Succeeded.
/// </summary>
public static class PayloadProbes
{
    // Comfortably larger than the small cap the spec configures.
    private static string BigValue() => new('x', 8 * 1024);

    /// <summary>Returns "caught" when an oversize variable write was rejected with the expected exception.</summary>
    [Job("oversize-variable-probe")]
    public static async Task<string> SetOversizeVariable(JobContext ctx, CancellationToken ct)
    {
        try
        {
            await ctx.SetVariableAsync("big", BigValue(), ct);
            return "not-caught";
        }
        catch (PayloadTooLargeException)
        {
            return "caught";
        }
    }

    /// <summary>Returns an oversize result; the framework drops it with a warning and completes the job, never throwing.</summary>
    [Job("oversize-result-probe")]
    public static Task<string> ReturnOversizeResult(JobContext ctx, CancellationToken ct) => Task.FromResult(BigValue());

    /// <summary>
    /// Returns "old" from its first execution and, from any later one, a string past the default cap, whose result is
    /// dropped: a restarted job then succeeds again with an older execution's result still stored.
    /// </summary>
    [Job("old-then-oversize-result-probe")]
    public static Task<string> ReturnOldThenOversizeResult(OldThenOversizeInput input, JobContext ctx, CancellationToken ct) =>
        Task.FromResult(ctx.ExecutionNumber == 1 ? "old" : new string('x', new JobsOptions().MaxInlinePayloadBytes + 1));
}

public sealed record OldThenOversizeInput(int Ignored);
