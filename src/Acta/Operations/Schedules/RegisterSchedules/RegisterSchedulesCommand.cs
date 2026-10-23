namespace Acta;

/// <summary>
/// Validated startup reconcile upsert: every definition is in <c>NamespaceId</c>. <c>SlotRefs</c> is
/// positionally aligned with <c>Definitions</c>: a C#-allocated public ref per slot, used only when
/// the slot job row is freshly inserted (an existing slot keeps its stored ref). <c>CatalogHash</c> is
/// stored on the namespace in the same transaction.
/// </summary>
internal sealed record RegisterSchedulesCommand(
    int NamespaceId,
    IReadOnlyList<DefinitionSchedules> Definitions,
    IReadOnlyList<Guid> SlotRefs,
    string? CatalogHash
);
