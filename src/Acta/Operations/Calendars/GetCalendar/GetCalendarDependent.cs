namespace Acta;

/// <summary>
/// A schedule bound to the family through the catalog: its path, how it binds the family (as its occurrence source
/// or its availability filter), its status (orphaned ones are listed), the version it pins, and its definition's
/// delivery. A shared family's dependents span namespaces, so the path carries the namespace.
/// </summary>
public sealed record GetCalendarDependent(
    string JobNamespace,
    string JobName,
    string ScheduleName,
    CalendarRoleCode Binding,
    ScheduleStatusCode Status,
    int PinnedVersion,
    ScheduledDeliveryCode ScheduledDelivery
);
