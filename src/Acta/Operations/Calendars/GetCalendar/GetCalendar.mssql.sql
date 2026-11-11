-- The family's head: its namespace, its current (Published) version, and its latest; no row for an unknown family.
SELECT
    c.namespace_id,
    MAX(CASE WHEN c.calendar_status_code = 20 /* CalendarStatusCode.Published */ THEN c.version END) AS current_version,
    MAX(c.version) AS latest_version
FROM {{schema}}.calendars c WITH (FORCESEEK (ux_calendars_namespace_name_version (namespace_id, calendar_name)))
WHERE
    c.namespace_id = (
        SELECT n.namespace_id
        FROM {{schema}}.namespaces n WITH (FORCESEEK (ux_namespaces_name (namespace_name)))
        WHERE n.namespace_name = @p_namespace_name
    )
    AND c.calendar_name = @p_calendar_name
GROUP BY c.namespace_id;
