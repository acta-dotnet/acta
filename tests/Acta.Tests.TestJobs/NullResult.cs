using Acta;

namespace TestJobs;

public sealed record NullResultInput(int Ignored);

public sealed record NullResultOutput(int Value);

/// <summary>
/// Probe for the null-result contract: a handler that returns <c>null</c> from a non-null result type.
/// The framework fails the job (it is never stored as a result). MaxAttempts = 1 so the failure is
/// terminal in one tick.
/// </summary>
public static class NullResultHandler
{
    [Job("null-result", MaxAttempts = 1)]
    public static NullResultOutput Run(NullResultInput input) => null!;
}
