namespace Acta.Runtime;

/// <summary>The namespace's persisted per-schedule cursors.</summary>
internal sealed record GetScheduleStateResponse(IReadOnlyList<StoredScheduleState> States);
