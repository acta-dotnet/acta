SELECT
    c.calendar_id,
    ns.namespace_name AS namespace,
    c.calendar_name,
    c.version,
    {{decode:calendar-role:c.calendar_role_code}} AS role,
    c.calendar_role_code,
    {{decode:calendar-status:c.calendar_status_code}} AS status,
    c.calendar_status_code,
    c.content_hash,
    c.created_at_utc,
    c.published_at_utc,
    c.modified_at_utc
FROM {{schema}}.calendars AS c
JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = c.namespace_id
