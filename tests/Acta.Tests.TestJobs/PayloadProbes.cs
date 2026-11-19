using Acta;

namespace TestJobs;

/// <summary>
/// Handlers exercising <c>MaxInlinePayloadBytes</c> inside a running handler: an oversize variable write must throw
/// <c>PayloadTooLargeException</c>, since the handler sends it, while a result past the cap is stored, since its body has
/// already run.
/// </summary>
public static class PayloadProbes
{
    // Comfortably larger than the small cap the spec configures.
    private static string BigValue() => new('x', 8 * 1024);

    /// <summary>A text of <paramref name="length"/> letters in a repeating run, so a read that lost or reordered bytes differs.</summary>
    public static string Text(int length) => string.Create(length, 0, static (chars, _) =>
        {
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = (char)('a' + (i % 26));
            }
        });

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

    /// <summary>Returns <see cref="Text"/> of the length its input names, which a spec sets past the cap.</summary>
    [Job("oversize-result-probe")]
    public static Task<string> ReturnOversizeResult(OversizeResultInput input, JobContext ctx, CancellationToken ct) =>
        Task.FromResult(Text(input.Length));

    /// <summary>
    /// Returns "old" from its first execution and throws a never-retried exception from any later one: a restarted job
    /// fails for good, and no read answers the first execution's "old" for it.
    /// </summary>
    [Job("old-then-failing-probe")]
    public static Task<string> ReturnOldThenFail(OldThenFailingInput input, JobContext ctx, CancellationToken ct) =>
        ctx.ExecutionNumber == 1 ? Task.FromResult("old") : throw new NotSupportedException("The second execution fails.");
}

public sealed record OversizeResultInput(int Length);

public sealed record OldThenFailingInput(int Ignored);
