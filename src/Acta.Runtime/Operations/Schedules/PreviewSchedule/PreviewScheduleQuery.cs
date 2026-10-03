namespace Acta.Runtime;

/// <summary>The schedule to preview, and how many upcoming occurrences to walk.</summary>
internal sealed record PreviewScheduleQuery(ScheduleLookup Schedule, int Count);
