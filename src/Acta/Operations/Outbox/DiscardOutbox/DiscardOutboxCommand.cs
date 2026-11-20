namespace Acta;

/// <summary>A discard of a source's quarantined rows to park: the rows <c>JobRefs</c> names, or every one when <c>All</c> is set.</summary>
internal sealed record DiscardOutboxCommand(
    string JobNamespace,
    IReadOnlyList<Guid>? JobRefs,
    bool All,
    string? ReasonMessage,
    string? Actor
);
