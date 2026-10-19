namespace Acta.Testing;

/// <summary>
/// What one test-driven recovery pass for a namespace changed.
/// </summary>
public sealed record ActaRecoveryReport(int DeadWorkersMarked, int ReclaimedJobs, int ReleasedChildLatches);
