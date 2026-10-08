namespace Acta.Runtime;

/// <summary>The recurring slot of every definition registered, one per definition.</summary>
internal readonly record struct RegisterSchedulesResponse(IReadOnlyList<RegisterSchedulesRow> Slots);
