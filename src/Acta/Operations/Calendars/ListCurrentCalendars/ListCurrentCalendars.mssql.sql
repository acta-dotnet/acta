SELECT
    c.calendar_name,
    c.calendar_role_code AS role,
    c.version
FROM {{schema}}.calendars c WITH (FORCESEEK (ux_calendars_current (namespace_id)))
WHERE
    c.namespace_id IN (@p_namespace_id, 1 /* NameSyntax.SystemNamespaceId */)
    AND c.calendar_status_code = 20 /* CalendarStatusCode.Published */
ORDER BY c.calendar_name;
