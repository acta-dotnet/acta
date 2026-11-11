using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Acta.Verify;

/// <summary>
/// The crash-recovery shapes the chaos scenario seeds. Each body writes the witness notes certify.sql reads, and each returns what
/// <see cref="WorkloadModel"/> computes for its input, so the identity effects stays the one home of the expectation.
/// </summary>
public sealed class ChaosJobs(IJobs jobs)
{
    /// <summary>The crash victims: durable steps, so a reclaimed attempt shows that recorded steps do not run twice.</summary>
    [Job("slow-success")]
    public async Task<string> SlowSuccess(WorkInput input, JobContext ctx, CancellationToken ct)
    {
        // The tenant the handler saw after enqueue, claim, dispatch and decode; certify.sql check 13 compares it.
        await ctx.NoteAsync($"tenant {ctx.TenantName ?? "-"}", ct);
        return await StepsAsync(input, ctx, ct);
    }

    /// <summary>A lane member; the begin and end notes witness that members of one lane never overlap.</summary>
    [Job("laned")]
    public async Task<string> Laned(WorkInput input, JobContext ctx, CancellationToken ct)
    {
        await ctx.NoteAsync("begin " + input.Key, ct);
        var result = await StepsAsync(input, ctx, ct);
        await ctx.NoteAsync("end " + input.Key, ct);
        return result;
    }

    [Job("metered-10", RateLimit = "10/s")]
    public static Task<string> Metered10(WorkInput input, JobContext ctx, CancellationToken ct) => AdmitAsync(input, ctx, ct);

    [Job("metered-50", RateLimit = "50/s")]
    public static Task<string> Metered50(WorkInput input, JobContext ctx, CancellationToken ct) => AdmitAsync(input, ctx, ct);

    [Job("metered-100", RateLimit = "100/s")]
    public static Task<string> Metered100(WorkInput input, JobContext ctx, CancellationToken ct) => AdmitAsync(input, ctx, ct);

    /// <summary>
    /// The charge: its one effect runs zero or one times. A kill inside the body terminalizes the ambiguity as Failed
    /// instead of charging again, so the effects allows Failed with nothing committed.
    /// </summary>
    [Job("at-most-once-charge", MaxAttempts = 3, Backoff = "2s..4s")]
    public static async Task<string> Charge(WorkInput input, JobContext ctx, CancellationToken ct)
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

    private async Task<string> StepsAsync(WorkInput input, JobContext ctx, CancellationToken ct)
    {
        long sum = 0;
        for (var i = 1; i <= input.Steps; i++)
        {
            var step = i;
            var name = $"step-{step}";
            sum += await ctx.RunStepAsync(
                name,
                async token =>
                {
                    await NoteStepBodyAsync(ctx, name, token);
                    await Task.Delay(input.WorkMs, token);
                    return step * 7L;
                },
                ct: ct
            );

            // The step returned, so its success committed: certify.sql check 7 reads a body run in a later attempt than
            // this note as a replay.
            await ctx.NoteAsync($"step-done {name}", ct);
        }
        return sum.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The witness note a step body writes before its work, one per admitted body, which certify.sql check 7 reads.</summary>
    private async Task NoteStepBodyAsync(JobContext ctx, string name, CancellationToken ct)
    {
        // A body that finds its step already succeeded says so, and the read is exact. A body the engine legitimately
        // re-runs, whose success never committed, always reads Pending: start_job_step hands over a body only for a
        // Pending row, and a stale attempt cannot flip that row while this attempt holds the job, because the execution
        // fence refuses its writes. A stale attempt that reads Succeeded is refused this note by the same fence.
        var succeeded = await HasSucceededAsync(ctx.JobId, name, ct);
        await ctx.NoteAsync(succeeded ? $"step-body-after-success {name}" : $"step-body {name}", ct);
    }

    private async Task<bool> HasSucceededAsync(long jobId, string name, CancellationToken ct)
    {
        string? cursor = null;
        do
        {
            var page = await jobs.ListStepsAsync(new ListJobStepsQuery(JobLookup.ById(jobId), Cursor: cursor), ct);
            if (page.Items.FirstOrDefault(s => s.Name == name) is { } found)
            {
                return found.Status == StepStatusCode.Succeeded;
            }
            cursor = page.NextCursor;
        } while (cursor is not null);
        return false;
    }

    private static async Task<string> AdmitAsync(WorkInput input, JobContext ctx, CancellationToken ct)
    {
        // Written first, so it stands for the instant the meter admitted this attempt; certify.sql check 14 counts them.
        await ctx.NoteAsync("metered-admitted", ct);
        await Task.Delay(input.WorkMs, ct);
        return Hash(input.Key);
    }

    private static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
