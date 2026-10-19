namespace Acta;

/// <summary>The claimed job, or none.</summary>
internal sealed record ClaimJobResponse(IReadOnlyList<ClaimedJob> Jobs)
{
    public static readonly ClaimJobResponse Empty = new([]);
}
