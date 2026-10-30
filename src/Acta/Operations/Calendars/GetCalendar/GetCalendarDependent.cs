namespace Acta;

/// <summary>
/// A schedule that names the family: its path, how it names the family (as its occurrence source or its availability
/// filter), its status (orphaned ones are listed), and its definition's delivery. A shared family's dependents span
/// namespaces, so the path carries the namespace. The name resolves at every evaluation, a calendar declared in the
/// process before the catalog's, so a dependent follows the family's current version unless its process declares one.
/// </summary>
public sealed record GetCalendarDependent(
    string JobNamespace,
    string JobName,
    string ScheduleName,
    CalendarRoleCode Binding,
    ScheduleStatusCode Status,
    ScheduledDeliveryCode ScheduledDelivery
);
