using Acta;

namespace TestJobs;

public sealed record NullResultInput(int Ignored);

public sealed record NullResultOutput(int Value);

public sealed class UnserializableOutput(long jobId)
{
    public long Value => jobId < 0 ? jobId : throw new InvalidOperationException("this value cannot be read");
}

/// <summary>
/// Probe for the null-result contract: a handler that returns <c>null</c> from a non-null result type.
/// The framework fails the job for good on that attempt (it is never stored as a result), with the default budget's
/// other attempts unspent.
/// </summary>
public static class NullResultHandler
{
    [Job("null-result")]
    public static NullResultOutput Run(NullResultInput input) => null!;

    /// <summary>Returns a value whose serialization throws.</summary>
    [Job("unserializable-result")]
    public static Task<UnserializableOutput> RunUnserializable(JobContext ctx, CancellationToken ct) =>
        Task.FromResult(new UnserializableOutput(ctx.JobId));
}
