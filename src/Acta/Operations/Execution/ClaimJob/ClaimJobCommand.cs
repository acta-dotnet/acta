namespace Acta;

/// <summary>One claim: the Ready job <c>JobId</c> names, or the next one by the scan when it is null.</summary>
internal sealed record ClaimJobCommand(ClaimOptions Options, int LeaseTtlSeconds, long? JobId);
