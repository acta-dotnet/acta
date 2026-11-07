namespace Acta;

/// <summary>
/// What saving a draft did: applied carries the new version and its content hash, and already-in-state the latest version, which
/// holds the same definition, and its hash; rejected and version-conflict carry each reason in problems, a compiler problem
/// starting with its JSON path. A namespace no worker has run in yet is created, so the save never answers not-found.
/// </summary>
/// <param name="Action">What the call did: applied; already-in-state, when the family's latest version holds the same definition and
/// is not archived, so nothing was saved; rejected, when the role is not the family's, the other scope holds the name, or, over HTTP,
/// the definition does not compile; or version-conflict, when another version is the family's latest.</param>
/// <param name="Version">The number of the draft version that was saved; for already-in-state, the latest version, which holds the
/// definition; when the store refused the draft, as version-conflict or rejected, the family's latest version, 0 for a family with
/// none; null when the definition does not compile.</param>
/// <param name="ContentHash">A hash of the definition: the saved one, or the latest version's for already-in-state; null when the
/// draft was refused.</param>
/// <param name="Problems">Why the draft was not saved, one sentence each; empty when it was saved or already held.</param>
public sealed record SaveCalendarDraftResponse(ControlAction Action, int? Version, string? ContentHash, IReadOnlyList<string> Problems)
    : ICalendarControlResponse;
