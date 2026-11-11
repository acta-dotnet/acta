-- A family's rows share its role, so the role filter may test any row; the status filter keeps only a family's latest row.
-- The page groups the rows by namespace and name, then reads each family's latest and current rows by key. A namespace's
-- list includes the shared families in sys, and two namespaces may each hold a name, so the namespace name breaks a tie.
DECLARE @namespace_id INT = (
    SELECT n.namespace_id
    FROM {{schema}}.namespaces n WITH (FORCESEEK (ux_namespaces_name (namespace_name)))
    WHERE n.namespace_name = @p_namespace_name
);
-- The listed namespaces as one range of ids, the named one alone or every one, so each calendars read below can seek.
DECLARE @first_namespace_id INT = IIF(@p_namespace_name IS NULL, 0, @namespace_id);
DECLARE @last_namespace_id INT = IIF(@p_namespace_name IS NULL, 2147483647, @namespace_id);

SELECT
    f.namespace_name AS job_namespace,
    f.calendar_name,
    f.role,
    cur.version AS current_version,
    lat.version AS latest_version,
    lat.calendar_status_code AS latest_status,
    cur.published_at_utc,
    lat.modified_at_utc,
    -- A namespace's family counts its own schedules, sought from the namespace's definitions as an archive counts them; a
    -- shared family's span every namespace, which no index reaches, so they are read as ListCalendarDependents reads them.
    CASE
        WHEN f.namespace_id = 1 /* NameSyntax.SystemNamespaceId */ THEN (
            SELECT COUNT(*)
            FROM {{schema}}.schedules s WITH (READCOMMITTEDLOCK, READPAST)
            INNER JOIN {{schema}}.definitions d WITH (READCOMMITTEDLOCK, READPAST) ON d.definition_id = s.definition_id
            WHERE
                s.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
                AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
                AND (
                    (
                        s.expression_kind_code = 30 /* ScheduleExpressionKindCode.Calendar */
                        AND s.expression = f.calendar_name
                    )
                    OR s.availability_calendar_name = f.calendar_name
                )
        )
        ELSE (
            SELECT COUNT(*)
            FROM {{schema}}.definitions d WITH (FORCESEEK (ux_definitions_namespace_name (namespace_id)))
            INNER JOIN {{schema}}.schedules s WITH (FORCESEEK (ux_schedules_definition_name (definition_id)))
                ON s.definition_id = d.definition_id
            WHERE
                d.namespace_id = f.namespace_id
                AND s.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
                AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
                AND (
                    (
                        s.expression_kind_code = 30 /* ScheduleExpressionKindCode.Calendar */
                        AND s.expression = f.calendar_name
                    )
                    OR s.availability_calendar_name = f.calendar_name
                )
        )
    END AS dependent_count
FROM (
    SELECT TOP (@p_take)
        c.namespace_id,
        ns.namespace_name,
        c.calendar_name,
        MIN(c.calendar_role_code) AS role,
        MAX(c.version) AS latest_version
    FROM {{schema}}.calendars c WITH (FORCESEEK (ux_calendars_namespace_name_version (namespace_id)))
    JOIN {{schema}}.namespaces ns WITH (FORCESEEK) ON ns.namespace_id = c.namespace_id
    WHERE
        (c.namespace_id = 1 /* NameSyntax.SystemNamespaceId */ OR c.namespace_id BETWEEN @first_namespace_id AND @last_namespace_id)
        AND (@p_calendar_role_code IS NULL OR c.calendar_role_code = @p_calendar_role_code)
        AND (@p_name_search IS NULL OR c.calendar_name LIKE @p_name_search)
        AND (
            @p_calendar_status_code IS NULL
            OR (
                c.calendar_status_code = @p_calendar_status_code
                AND NOT EXISTS (
                    SELECT 1
                    FROM {{schema}}.calendars newer WITH (FORCESEEK)
                    WHERE
                        newer.namespace_id = c.namespace_id
                        AND newer.calendar_name = c.calendar_name
                        AND newer.version > c.version
                )
            )
        )
        AND (@p_cursor_calendar_name IS NULL
            OR c.calendar_name > @p_cursor_calendar_name
            OR (c.calendar_name = @p_cursor_calendar_name AND ns.namespace_name > @p_cursor_namespace_name))
    GROUP BY c.namespace_id, ns.namespace_name, c.calendar_name
    ORDER BY c.calendar_name ASC, ns.namespace_name ASC
) f
JOIN {{schema}}.calendars lat WITH (FORCESEEK)
    ON
        lat.namespace_id = f.namespace_id
        AND lat.calendar_name = f.calendar_name
        AND lat.version = f.latest_version
LEFT JOIN {{schema}}.calendars cur WITH (FORCESEEK)
    ON
        cur.namespace_id = f.namespace_id
        AND cur.calendar_name = f.calendar_name
        AND cur.calendar_status_code = 20 /* CalendarStatusCode.Published */
ORDER BY f.calendar_name ASC, f.namespace_name ASC;

SELECT CASE WHEN @p_include_total IS NOT NULL THEN (
    SELECT COUNT(*)
    FROM (
        SELECT c.namespace_id, c.calendar_name
        FROM {{schema}}.calendars c WITH (FORCESEEK (ux_calendars_namespace_name_version (namespace_id)))
        WHERE
            (c.namespace_id = 1 /* NameSyntax.SystemNamespaceId */ OR c.namespace_id BETWEEN @first_namespace_id AND @last_namespace_id)
            AND (@p_calendar_role_code IS NULL OR c.calendar_role_code = @p_calendar_role_code)
            AND (@p_name_search IS NULL OR c.calendar_name LIKE @p_name_search)
            AND (
                @p_calendar_status_code IS NULL
                OR (
                    c.calendar_status_code = @p_calendar_status_code
                    AND NOT EXISTS (
                        SELECT 1
                        FROM {{schema}}.calendars newer WITH (FORCESEEK)
                        WHERE
                            newer.namespace_id = c.namespace_id
                            AND newer.calendar_name = c.calendar_name
                            AND newer.version > c.version
                    )
                )
            )
        GROUP BY c.namespace_id, c.calendar_name
    ) families
) END;
