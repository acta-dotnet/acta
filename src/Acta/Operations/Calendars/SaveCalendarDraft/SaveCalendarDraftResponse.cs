namespace Acta;

/// <summary>
/// What saving a draft did: applied carries the new version and its content hash; rejected and version-conflict carry each reason in
/// problems, a compiler problem starting with its JSON path, and, when the store refused the draft, the family's latest version, 0 for
/// a family with none. A namespace no worker has run in yet is created, so the save never answers not-found.
/// </summary>
/// <param name="Action">What the call did: applied; rejected, when the definition does not compile, the role is not the family's, or
/// the other scope holds the name; or version-conflict, when another version is the family's latest.</param>
/// <param name="Version">The number of the draft version that was saved; when the store refused the draft, as version-conflict or
/// rejected, the family's latest version, 0 for a family with none; null when the definition does not compile.</param>
/// <param name="ContentHash">A hash of the saved definition; null when nothing was saved.</param>
/// <param name="Problems">Why the draft was not saved, one sentence each; empty when it was saved.</param>
public sealed record SaveCalendarDraftResponse(ControlAction Action, int? Version, string? ContentHash, IReadOnlyList<string> Problems)
    : ICalendarControlResponse;
