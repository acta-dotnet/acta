namespace Acta;

/// <summary>A discard of a source's quarantined rows to park: the rows <c>OutboxIds</c> names, or every one when <c>All</c> is set.</summary>
internal sealed record DiscardOutboxCommand(
    string JobNamespace,
    IReadOnlyList<Guid>? OutboxIds,
    bool All,
    string? ReasonMessage,
    string? Actor
);
