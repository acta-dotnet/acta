using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Acta.Bench;

/// <summary>
/// The crash-recovery shapes F09 seeds. Each body writes the witness notes certify.sql reads, and each returns what
/// <see cref="WorkloadModel"/> computes for its input, so the identity oracle stays the one home of the expectation.
/// </summary>
public static class ChaosJobs
{
    /// <summary>The crash victims: durable steps, so a reclaimed attempt shows that recorded steps do not run twice.</summary>
    [Job("slow-success")]
    public static async Task<string> SlowSuccess(BenchWork input, JobContext ctx, CancellationToken ct)
    {
        // The tenant the handler saw after enqueue, claim, dispatch and decode; certify.sql check 13 compares it.
        await ctx.NoteAsync($"tenant {ctx.TenantName ?? "-"}", ct);
        return await StepsAsync(input, ctx, ct);
    }

    /// <summary>A lane member; the begin and end notes witness that members of one lane never overlap.</summary>
    [Job("laned")]
    public static async Task<string> Laned(BenchWork input, JobContext ctx, CancellationToken ct)
    {
        await ctx.NoteAsync("begin " + input.Key, ct);
        var result = await StepsAsync(input, ctx, ct);
        await ctx.NoteAsync("end " + input.Key, ct);
        return result;
    }

    [Job("metered-10", RateLimit = "10/s")]
    public static Task<string> Metered10(BenchWork input, JobContext ctx, CancellationToken ct) => AdmitAsync(input, ctx, ct);

    [Job("metered-50", RateLimit = "50/s")]
    public static Task<string> Metered50(BenchWork input, JobContext ctx, CancellationToken ct) => AdmitAsync(input, ctx, ct);

    [Job("metered-100", RateLimit = "100/s")]
    public static Task<string> Metered100(BenchWork input, JobContext ctx, CancellationToken ct) => AdmitAsync(input, ctx, ct);

    /// <summary>
    /// The charge: its one effect runs zero or one times. A kill inside the body terminalizes the ambiguity as Failed
    /// instead of charging again, so the oracle allows Failed with nothing committed.
    /// </summary>
    [Job("at-most-once-charge", MaxAttempts = 3, Backoff = "2s..4s")]
    public static async Task<string> Charge(BenchWork input, JobContext ctx, CancellationToken ct)
    {
        await ctx.NoteAsync($"tenant {ctx.TenantName ?? "-"}", ct);
        await ctx.RunStepAsync(
            "effect",
            async token =>
            {
                // Its own operation, so the note survives when the step's outcome does not.
                await ctx.NoteAsync("charge-body", token);
                await Task.Delay(input.WorkMs, token);
            },
            options => options.AtMostOnce(),
            ct
        );
        return Hash(input.Key);
    }

    private static async Task<string> StepsAsync(BenchWork input, JobContext ctx, CancellationToken ct)
    {
        long sum = 0;
        for (var i = 1; i <= input.Steps; i++)
        {
            var step = i;
            sum += await ctx.RunStepAsync(
                $"step-{step}",
                async token =>
                {
                    // One note per admitted body: certify.sql check 7 counts them against the step's attempts.
                    await ctx.NoteAsync($"step-body step-{step}", token);
                    await Task.Delay(input.WorkMs, token);
                    return step * 7L;
                },
                ct: ct
            );
        }
        return sum.ToString(CultureInfo.InvariantCulture);
    }

    private static async Task<string> AdmitAsync(BenchWork input, JobContext ctx, CancellationToken ct)
    {
        // Written first, so it stands for the instant the meter admitted this attempt; certify.sql check 14 counts them.
        await ctx.NoteAsync("metered-admitted", ct);
        await Task.Delay(input.WorkMs, ct);
        return Hash(input.Key);
    }

    private static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
