namespace Acta;

/// <summary>
/// The claimed rows, plus the horizon when nothing was claimed. Exactly one of the two carries information: a
/// non-empty <c>Jobs</c> has a null <c>Horizon</c>, and an empty claim has it set (the routine emits one sentinel
/// row in place of job rows).
/// </summary>
internal sealed record ClaimJobsResponse(IReadOnlyList<ClaimedJob> Jobs, ClaimHorizon? Horizon)
{
    public static readonly ClaimJobsResponse Empty = new([], null);
}
