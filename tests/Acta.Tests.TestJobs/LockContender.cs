using Acta;

namespace TestJobs;

/// <summary>What a contender does once it is inside the lock: <c>return</c>, <c>throw</c>, or <c>wait</c> on a signal.</summary>
public sealed record LockContention(string Inside);

/// <summary>
/// Takes one attempt at the lock <see cref="LockHolder"/> holds and, once inside, notes it and then returns, throws, or
/// suspends on a signal. One attempt and a budget of one, so a lock another job is inside fails the job at once.
/// </summary>
public static class LockContender
{
    public const string InsideNote = "inside the lock";

    [Job("lock-contender", MaxAttempts = 1)]
    public static Task Run(LockContention input, JobContext ctx, CancellationToken ct) =>
        ctx.RunWithLockAsync(
            LockHolder.LockName,
            async () =>
            {
                await ctx.NoteAsync(InsideNote, ct);
                switch (input.Inside)
                {
                    case "throw":
                        throw new InvalidOperationException("the locked action failed");
                    case "wait":
                        await ctx.WaitSignalAsync("go", ct);
                        break;
                }
            },
            timeout: TimeSpan.Zero,
            ct: ct
        );
}
