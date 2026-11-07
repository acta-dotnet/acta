namespace Acta;

/// <summary>
/// A schedule that names the calendar family: where it is, how it names the family, its status, and how its definition delivers work. A
/// shared family's dependents span namespaces.
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
