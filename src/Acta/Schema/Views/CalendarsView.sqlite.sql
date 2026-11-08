SELECT
    c.calendar_id,
    ns.namespace_name,
    c.calendar_name,
    c.version,
    {{decode:calendar-role:c.calendar_role_code}} AS calendar_role,
    c.calendar_role_code,
    {{decode:calendar-status:c.calendar_status_code}} AS calendar_status,
    c.calendar_status_code,
    c.content_hash,
    strftime('%Y-%m-%dT%H:%M:%fZ', c.created_at_utc / 1000.0, 'unixepoch') AS created_at_utc,
    strftime('%Y-%m-%dT%H:%M:%fZ', c.published_at_utc / 1000.0, 'unixepoch') AS published_at_utc,
    strftime('%Y-%m-%dT%H:%M:%fZ', c.modified_at_utc / 1000.0, 'unixepoch') AS modified_at_utc
FROM {{schema}}.calendars AS c
JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = c.namespace_id
