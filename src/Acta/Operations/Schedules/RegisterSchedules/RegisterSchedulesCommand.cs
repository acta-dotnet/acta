using System.Data.Common;

namespace Acta;

/// <summary>
/// Validated reconcile upsert a deploy makes: every definition is in <c>NamespaceId</c>. <c>SlotRefs</c> is
/// positionally aligned with <c>Definitions</c>: a C#-allocated public ref per slot, used only when
/// the slot job row is freshly inserted (an existing slot keeps its stored ref). It joins <c>Transaction</c>
/// when a deploy owns one.
/// </summary>
internal sealed record RegisterSchedulesCommand(
    int NamespaceId,
    IReadOnlyList<DefinitionSchedules> Definitions,
    IReadOnlyList<Guid> SlotRefs,
    DbTransaction? Transaction = null
);
