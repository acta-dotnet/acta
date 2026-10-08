namespace Acta;

/// <summary>The priority-ordered claim scan, leasing each claimed row for <c>LeaseTtlSeconds</c>.</summary>
internal sealed record ClaimBatchCommand(ClaimRequest Request, int LeaseTtlSeconds);
