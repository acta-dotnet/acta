namespace Acta;

/// <summary>
/// Definition/slot id pair returned by <c>register_schedules</c>.
/// </summary>
[DbProjection]
internal readonly record struct RegisterSchedulesRow(int DefinitionId, long SlotId);
