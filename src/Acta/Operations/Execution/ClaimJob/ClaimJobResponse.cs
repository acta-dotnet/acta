namespace Acta;

/// <summary>
/// The claimed job, or none; a claim that names no job is the claim scan's, and carries its horizon when it
/// claims nothing.
/// </summary>
internal sealed record ClaimJobResponse(IReadOnlyList<ClaimedJob> Jobs, ClaimHorizon? Horizon)
{
    public static readonly ClaimJobResponse Empty = new([], null);
}
