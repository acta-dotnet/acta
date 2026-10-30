-- A family's rows share its role, so the role filter may test any row; the page groups the rows by namespace and name,
-- then reads each family's latest and current rows by key. A namespace's list includes the shared families in sys, and
-- two namespaces may each hold a name, so the namespace name breaks a tie in the order.
SELECT
    f.namespace_name AS job_namespace,
    f.calendar_name,
    f.role,
    cur.version AS current_version,
    lat.version AS latest_version,
    lat.calendar_status_code AS latest_status,
    cur.published_at_utc,
    lat.modified_at_utc,
    (
        SELECT COUNT(*)
        FROM {{schema}}.schedules s
        WHERE
            (s.namespace_id = f.namespace_id OR f.namespace_id = 1 /* NameSyntax.SystemNamespaceId */)
            AND s.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
            AND (
                (
                    s.expression_kind_code = 30 /* ScheduleExpressionKindCode.Calendar */
                    AND s.expression = f.calendar_name
                )
                OR s.availability_calendar_name = f.calendar_name
            )
    ) AS dependent_count
FROM (
    SELECT
        c.namespace_id,
        ns.namespace_name,
        c.calendar_name,
        MIN(c.calendar_role_code) AS role,
        MAX(c.version) AS latest_version
    FROM {{schema}}.calendars c
    JOIN {{schema}}.namespaces ns ON ns.namespace_id = c.namespace_id
    WHERE
        (
            @p_namespace_name IS NULL
            OR c.namespace_id = 1 /* NameSyntax.SystemNamespaceId */
            OR c.namespace_id = (SELECT n.namespace_id FROM {{schema}}.namespaces n WHERE n.namespace_name = @p_namespace_name)
        )
        AND (@p_calendar_role_code IS NULL OR c.calendar_role_code = @p_calendar_role_code)
        AND (@p_name_search IS NULL OR c.calendar_name LIKE @p_name_search)
        AND (@p_cursor_calendar_name IS NULL
            OR c.calendar_name > @p_cursor_calendar_name
            OR (c.calendar_name = @p_cursor_calendar_name AND ns.namespace_name > @p_cursor_namespace_name))
    GROUP BY c.namespace_id, ns.namespace_name, c.calendar_name
    ORDER BY c.calendar_name ASC, ns.namespace_name ASC
    LIMIT @p_take
) f
JOIN {{schema}}.calendars lat
    ON
        lat.namespace_id = f.namespace_id
        AND lat.calendar_name = f.calendar_name
        AND lat.version = f.latest_version
LEFT JOIN {{schema}}.calendars cur
    ON
        cur.namespace_id = f.namespace_id
        AND cur.calendar_name = f.calendar_name
        AND cur.calendar_status_code = 20 /* CalendarStatusCode.Published */
ORDER BY f.calendar_name ASC, f.namespace_name ASC;

SELECT CASE WHEN @p_include_total IS NOT NULL THEN (
    SELECT COUNT(*)
    FROM (
        SELECT c.namespace_id, c.calendar_name
        FROM {{schema}}.calendars c
        WHERE
            (
                @p_namespace_name IS NULL
                OR c.namespace_id = 1 /* NameSyntax.SystemNamespaceId */
                OR c.namespace_id = (SELECT n.namespace_id FROM {{schema}}.namespaces n WHERE n.namespace_name = @p_namespace_name)
            )
            AND (@p_calendar_role_code IS NULL OR c.calendar_role_code = @p_calendar_role_code)
            AND (@p_name_search IS NULL OR c.calendar_name LIKE @p_name_search)
        GROUP BY c.namespace_id, c.calendar_name
    ) families
) END;
