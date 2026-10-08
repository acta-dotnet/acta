namespace Acta;

/// <summary>Read options for <see cref="IJobs.GetLineageMapAsync"/>.</summary>
/// <param name="ChildLimit">Max direct children returned; the read fetches one extra to set
/// <see cref="GetJobLineageMapResponse.ChildrenHasMore"/>. Clamped to 1..1000; defaults to 100.</param>
public sealed record GetJobLineageMapOptions(int ChildLimit = 100);
