-- One row per binding: a schedule may name the family both as its calendar and as its availability filter.
SELECT
    n.namespace_name AS job_namespace,
    d.definition_name AS job_name,
    s.schedule_name,
    10 /* CalendarRoleCode.Occurrence */ AS binding,
    s.schedule_status_code AS status,
    d.scheduled_delivery_code AS scheduled_delivery
FROM {{schema}}.schedules s
JOIN {{schema}}.definitions d ON d.definition_id = s.definition_id
JOIN {{schema}}.namespaces n ON n.namespace_id = s.namespace_id
WHERE
    (s.namespace_id = @p_namespace_id OR @p_namespace_id = 1 /* NameSyntax.SystemNamespaceId */)
    AND s.expression_kind_code = 30 /* ScheduleExpressionKindCode.Calendar */
    AND s.expression = @p_calendar_name
UNION ALL
SELECT
    n.namespace_name AS job_namespace,
    d.definition_name AS job_name,
    s.schedule_name,
    20 /* CalendarRoleCode.Availability */ AS binding,
    s.schedule_status_code AS status,
    d.scheduled_delivery_code AS scheduled_delivery
FROM {{schema}}.schedules s
JOIN {{schema}}.definitions d ON d.definition_id = s.definition_id
JOIN {{schema}}.namespaces n ON n.namespace_id = s.namespace_id
WHERE
    (s.namespace_id = @p_namespace_id OR @p_namespace_id = 1 /* NameSyntax.SystemNamespaceId */)
    AND s.availability_calendar_name = @p_calendar_name
ORDER BY job_namespace, job_name, schedule_name, binding;
