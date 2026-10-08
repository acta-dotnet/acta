namespace Acta.Runtime;

/// <summary>
/// Validated startup reconcile upsert: all definitions share one namespace. <c>SlotRefs</c> is
/// positionally aligned with <c>Definitions</c>: a C#-allocated public ref per slot, used only when
/// the slot job row is freshly inserted (an existing slot keeps its stored ref).
/// </summary>
internal sealed record RegisterSchedulesCommand(IReadOnlyList<DefinitionSchedules> Definitions, IReadOnlyList<Guid> SlotRefs);
