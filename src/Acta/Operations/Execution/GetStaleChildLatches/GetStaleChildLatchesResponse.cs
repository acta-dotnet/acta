namespace Acta;

/// <summary>The Pending child latches whose child is already terminal or gone.</summary>
internal sealed record GetStaleChildLatchesResponse(IReadOnlyList<GetStaleChildLatchesItem> Latches);
