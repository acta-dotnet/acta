namespace Acta;

/// <summary>
/// A schedule that names the family: its path, how it names the family (as its occurrence source or its availability
/// filter), its status (orphaned ones are listed), and its definition's delivery. A shared family's dependents span
/// namespaces, so the path carries the namespace. The name resolves at every evaluation, a calendar declared in the
/// process before the catalog's, so a dependent follows the family's current version unless its process declares one.
/// </summary>
/// <param name="JobNamespace">The namespace of the schedule's definition.</param>
/// <param name="JobName">The name of the schedule's definition.</param>
/// <param name="ScheduleName">The schedule's name within its definition.</param>
/// <param name="Binding">The role in which the schedule names the family.</param>
/// <param name="Status">The schedule's status.</param>
/// <param name="ScheduledDelivery">How the schedule delivers work: by firing the definition's permanent slot job, or by enqueueing a new
/// job for each occurrence.</param>
public sealed record ListCalendarDependentsItem(
    string JobNamespace,
    string JobName,
    string ScheduleName,
    CalendarRoleCode Binding,
    ScheduleStatusCode Status,
    ScheduledDeliveryCode ScheduledDelivery
);
