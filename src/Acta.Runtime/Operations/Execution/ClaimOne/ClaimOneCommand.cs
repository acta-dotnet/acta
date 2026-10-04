namespace Acta.Runtime;

/// <summary>One claim: the Ready job <c>JobId</c> names, or the next one by the scan when it is null.</summary>
internal sealed record ClaimOneCommand(ClaimRequest Request, int LeaseTtlSeconds, long? JobId);
