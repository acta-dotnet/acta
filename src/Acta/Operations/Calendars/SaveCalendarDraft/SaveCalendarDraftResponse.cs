namespace Acta;

/// <summary>
/// What saving a draft did. Applied carries the new version and its content hash; Rejected carries each reason in
/// <c>Problems</c>, a compiler problem starting with its JSON path, and, when the store refused it, the family's latest
/// version, 0 for a family with none. A namespace no worker has run in yet is created, so the save never answers NotFound.
/// </summary>
/// <param name="Action">What the call did, such as applied, already-in-state, rejected, not-found, or version-conflict.</param>
/// <param name="Version">The number of the draft version that was saved; null when nothing was saved.</param>
/// <param name="ContentHash">A hash of the saved definition; null when nothing was saved.</param>
/// <param name="Problems">What is wrong with the definition, when it could not be saved.</param>
public sealed record SaveCalendarDraftResponse(ControlAction Action, int? Version, string? ContentHash, IReadOnlyList<string> Problems)
    : ICalendarControlResponse;
