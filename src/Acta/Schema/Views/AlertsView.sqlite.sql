SELECT
    a.alert_id,
    a.alert_ref,
    ns.namespace_name,
    a.job_id,
    a.job_ref,
    j.definition_id,
    d.definition_name,
    {{decode:alert-origin:a.alert_origin_code}} AS alert_origin,
    a.alert_origin_code,
    {{decode:alert-severity:a.alert_severity_code}} AS alert_severity,
    a.alert_severity_code,
    {{decode:alert-kind:a.alert_kind_code}} AS alert_kind,
    a.alert_kind_code,
    {{decode:alert-delivery-status:a.delivery_status_code}} AS delivery_status,
    a.delivery_status_code,
    a.title,
    a.alert_message,
    a.channel_name,
    a.deduplication_key,
    a.occurrence_count,
    strftime('%Y-%m-%dT%H:%M:%fZ', a.resolved_at_utc / 1000.0, 'unixepoch') AS resolved_at_utc,
    strftime('%Y-%m-%dT%H:%M:%fZ', a.acknowledged_at_utc / 1000.0, 'unixepoch') AS acknowledged_at_utc,
    a.retry_count,
    strftime('%Y-%m-%dT%H:%M:%fZ', a.retry_after_utc / 1000.0, 'unixepoch') AS retry_after_utc,
    strftime('%Y-%m-%dT%H:%M:%fZ', a.created_at_utc / 1000.0, 'unixepoch') AS created_at_utc,
    strftime('%Y-%m-%dT%H:%M:%fZ', a.modified_at_utc / 1000.0, 'unixepoch') AS modified_at_utc,
    a.version
FROM {{schema}}.alerts AS a
JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = a.namespace_id
LEFT JOIN {{schema}}.jobs AS j ON j.job_id = a.job_id
LEFT JOIN {{schema}}.definitions AS d ON d.definition_id = j.definition_id
