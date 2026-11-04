namespace Acta;

/// <summary>The namespace's persisted per-schedule cursors.</summary>
internal sealed record ListScheduleStatesResponse(IReadOnlyList<StoredScheduleState> States);
