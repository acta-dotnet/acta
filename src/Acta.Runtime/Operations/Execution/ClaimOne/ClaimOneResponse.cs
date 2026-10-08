namespace Acta.Runtime;

/// <summary>
/// The claimed job, or none; a claim that names no job is the claim scan's, and carries its horizon when it
/// claims nothing.
/// </summary>
internal sealed record ClaimOneResponse(IReadOnlyList<ClaimedJob> Jobs, ClaimHorizon? Horizon)
{
    public static readonly ClaimOneResponse Empty = new([], null);
}
