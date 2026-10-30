-- The family's current version and the one asked for, with their definitions; nothing when the namespace or the family
-- is unknown.
SELECT
    n.namespace_id,
    c.version,
    c.calendar_role_code AS role,
    c.calendar_status_code AS status,
    c.definition
FROM {{schema}}.namespaces n
INNER JOIN {{schema}}.calendars c ON c.namespace_id = n.namespace_id AND c.calendar_name = @p_calendar_name
WHERE
    n.namespace_name = @p_namespace_name
    AND (c.version = @p_version OR c.calendar_status_code = 20 /* CalendarStatusCode.Published */)
ORDER BY c.version;
