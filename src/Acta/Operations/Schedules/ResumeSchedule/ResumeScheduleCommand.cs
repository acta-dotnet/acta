namespace Acta;

/// <summary>The schedule to resume, with the operator's note and key for the audit event.</summary>
internal sealed record ResumeScheduleCommand(ScheduleLookup Schedule, string? ReasonMessage, string? ActorKey);
