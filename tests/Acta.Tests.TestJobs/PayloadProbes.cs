using Acta;

namespace TestJobs;

/// <summary>
/// Handlers exercising <c>MaxInlinePayloadBytes</c> enforcement inside a running handler: an oversize
/// variable write must throw <c>PayloadTooLargeException</c> (caller-controlled), while an oversize
/// handler result must warn-and-persist (the job still completes Done).
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

    /// <summary>Returns an oversize result; the framework must persist it (warn-and-persist), not throw.</summary>
    [Job("oversize-result-probe")]
    public static Task<string> ReturnOversizeResult(JobContext ctx, CancellationToken ct) => Task.FromResult(BigValue());
}
