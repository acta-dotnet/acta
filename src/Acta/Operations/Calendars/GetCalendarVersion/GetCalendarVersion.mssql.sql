SELECT
    c.calendar_role_code AS role,
    c.calendar_status_code AS status,
    c.definition
FROM {{schema}}.calendars c
WHERE
    c.namespace_id IN (@p_namespace_id, 1 /* NameSyntax.SystemNamespaceId */)
    AND c.calendar_name = @p_calendar_name
    AND c.version = @p_version;
