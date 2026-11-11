-- One keyset page of the schedules naming the family, by path then binding: a schedule naming it both as its calendar
-- and as its availability filter is two rows, and a shared family in sys binds the name in every namespace. No index
-- finds them, so for display each scan skips a row another transaction holds, a deploy's included.
SELECT TOP (@p_take)
    x.job_namespace,
    x.job_name,
    x.schedule_name,
    x.binding,
    x.status,
    x.scheduled_delivery
FROM (
    SELECT
        n.namespace_name AS job_namespace,
        d.definition_name AS job_name,
        s.schedule_name,
        10 /* CalendarRoleCode.Occurrence */ AS binding,
        s.schedule_status_code AS status,
        d.scheduled_delivery_code AS scheduled_delivery
    FROM {{schema}}.schedules s WITH (READCOMMITTEDLOCK, READPAST, ROWLOCK)
    JOIN {{schema}}.definitions d WITH (READCOMMITTEDLOCK, READPAST, ROWLOCK) ON d.definition_id = s.definition_id
    JOIN {{schema}}.namespaces n WITH (READCOMMITTEDLOCK, READPAST, ROWLOCK) ON n.namespace_id = s.namespace_id
    CROSS JOIN (
        SELECT DISTINCT f.namespace_id
        FROM {{schema}}.namespaces f
        INNER JOIN {{schema}}.calendars c ON c.namespace_id = f.namespace_id AND c.calendar_name = @p_calendar_name
        WHERE f.namespace_name = @p_namespace_name
    ) fam
    WHERE
        (s.namespace_id = fam.namespace_id OR fam.namespace_id = 1 /* NameSyntax.SystemNamespaceId */)
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
    FROM {{schema}}.schedules s WITH (READCOMMITTEDLOCK, READPAST, ROWLOCK)
    JOIN {{schema}}.definitions d WITH (READCOMMITTEDLOCK, READPAST, ROWLOCK) ON d.definition_id = s.definition_id
    JOIN {{schema}}.namespaces n WITH (READCOMMITTEDLOCK, READPAST, ROWLOCK) ON n.namespace_id = s.namespace_id
    CROSS JOIN (
        SELECT DISTINCT f.namespace_id
        FROM {{schema}}.namespaces f
        INNER JOIN {{schema}}.calendars c ON c.namespace_id = f.namespace_id AND c.calendar_name = @p_calendar_name
        WHERE f.namespace_name = @p_namespace_name
    ) fam
    WHERE
        (s.namespace_id = fam.namespace_id OR fam.namespace_id = 1 /* NameSyntax.SystemNamespaceId */)
        AND s.availability_calendar_name = @p_calendar_name
) x
WHERE
    @p_cursor_namespace_name IS NULL
    OR x.job_namespace > @p_cursor_namespace_name
    OR (
        x.job_namespace = @p_cursor_namespace_name
        AND (
            x.job_name > @p_cursor_job_name
            OR (
                x.job_name = @p_cursor_job_name
                AND (
                    x.schedule_name > @p_cursor_schedule_name
                    OR (x.schedule_name = @p_cursor_schedule_name AND x.binding > @p_cursor_binding)
                )
            )
        )
    )
ORDER BY x.job_namespace, x.job_name, x.schedule_name, x.binding;

-- One row per binding: a schedule naming the family both ways counts twice, as it lists twice.
SELECT CASE WHEN @p_include_total IS NOT NULL THEN (
    SELECT COUNT(*)
    FROM {{schema}}.schedules s WITH (READCOMMITTEDLOCK, READPAST, ROWLOCK)
    CROSS JOIN (
        SELECT DISTINCT f.namespace_id
        FROM {{schema}}.namespaces f
        INNER JOIN {{schema}}.calendars c ON c.namespace_id = f.namespace_id AND c.calendar_name = @p_calendar_name
        WHERE f.namespace_name = @p_namespace_name
    ) fam
    WHERE
        (s.namespace_id = fam.namespace_id OR fam.namespace_id = 1 /* NameSyntax.SystemNamespaceId */)
        AND s.expression_kind_code = 30 /* ScheduleExpressionKindCode.Calendar */
        AND s.expression = @p_calendar_name
) + (
    SELECT COUNT(*)
    FROM {{schema}}.schedules s WITH (READCOMMITTEDLOCK, READPAST, ROWLOCK)
    CROSS JOIN (
        SELECT DISTINCT f.namespace_id
        FROM {{schema}}.namespaces f
        INNER JOIN {{schema}}.calendars c ON c.namespace_id = f.namespace_id AND c.calendar_name = @p_calendar_name
        WHERE f.namespace_name = @p_namespace_name
    ) fam
    WHERE
        (s.namespace_id = fam.namespace_id OR fam.namespace_id = 1 /* NameSyntax.SystemNamespaceId */)
        AND s.availability_calendar_name = @p_calendar_name
) END;
