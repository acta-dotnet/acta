namespace Acta;

/// <summary>
/// What one claim on the hot priority-ordered scan asks for; by-id claiming is the separate <c>claim_job</c> path.
/// <c>ExcludedDefinitionIds</c>, the definitions this worker bounced for want of a handler, are skipped by the candidate
/// scan and the empty-claim horizon alike, so a worker left only excluded work sleeps instead of spinning at the floor.
/// <c>ClaimFromUtc</c> starts the candidate scan at that due instant, null at each band's head
/// (docs/sql-internals.md, "Claim watermark").
/// </summary>
internal sealed record ClaimOptions(
    int NamespaceId,
    int WorkerId,
    int MaxBatch,
    int[]? ExcludedDefinitionIds = null,
    DateTime? ClaimFromUtc = null
);
