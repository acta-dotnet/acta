SELECT
    c.namespace_id,
    c.calendar_role_code AS role,
    c.version,
    c.calendar_status_code AS status,
    c.content_hash,
    c.created_at_utc,
    c.published_at_utc,
    c.modified_at_utc
FROM {{schema}}.calendars c
WHERE
    c.namespace_id = (SELECT n.namespace_id FROM {{schema}}.namespaces n WHERE n.namespace_name = @p_namespace_name)
    AND c.calendar_name = @p_calendar_name
ORDER BY c.version DESC;
