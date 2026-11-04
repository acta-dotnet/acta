-- One keyset page of the family's versions, newest first.
SELECT TOP (@p_take)
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
    AND (@p_cursor_version IS NULL OR c.version < @p_cursor_version)
ORDER BY c.version DESC;

SELECT CASE WHEN @p_include_total IS NOT NULL THEN (
    SELECT COUNT(*)
    FROM {{schema}}.calendars c
    WHERE
        c.namespace_id = (SELECT n.namespace_id FROM {{schema}}.namespaces n WHERE n.namespace_name = @p_namespace_name)
        AND c.calendar_name = @p_calendar_name
) END;
