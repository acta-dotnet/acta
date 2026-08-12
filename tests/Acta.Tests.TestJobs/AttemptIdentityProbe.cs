using Acta;

namespace TestJobs;

/// <summary>
/// Records the attempt identity the running context reports - <c>ctx.ExecutionNumber</c> and
/// <c>ctx.WorkerId</c> - onto the job's own timeline, once per attempt. The first attempt throws so a
/// spec sees two notes and can prove the number advances rather than reading back a default.
/// </summary>
/// <remarks>
/// The retry gate reads the value under test on purpose: an unwired <c>ExecutionNumber</c> reports its
/// <c>1</c> default forever, so the job exhausts <c>MaxAttempts</c> and lands Failed instead of quietly
/// passing a spec that only compared two identical numbers.
/// </remarks>
public static class AttemptIdentityProbe
{
    public sealed record AttemptIdentity(int ExecutionNumber, int WorkerId);

    [Job("attempt-identity", MaxAttempts = 3, Backoff = "0s")]
    public static async Task Run(JobContext ctx, CancellationToken ct)
    {
        await ctx.NoteAsync("attempt identity", new AttemptIdentity(ctx.ExecutionNumber, ctx.WorkerId), ct);
        if (ctx.ExecutionNumber == 1)
        {
            throw new InvalidOperationException("attempt-identity fails its first attempt.");
        }
    }
}
