namespace Acta;

/// <summary>One draft as the facade received it: the family, its role, the definition JSON, and the latest version seen.</summary>
internal sealed record SaveCalendarDraftCommand(
    CalendarLookup Calendar,
    CalendarRoleCode Role,
    string Definition,
    int ExpectedLatestVersion,
    string? ReasonMessage,
    string? Actor
);
