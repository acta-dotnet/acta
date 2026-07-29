SELECT
    a.id AS alert_id,
    ns.name AS namespace,
    a.job_id,
    a.job_ref,
    j.definition_id,
    d.name AS job_name,
    {{decode:alert-origin:a.origin_code}} AS origin,
    a.origin_code,
    {{decode:alert-severity:a.severity_code}} AS severity,
    a.severity_code,
    {{decode:alert-kind:a.kind_code}} AS kind,
    a.kind_code,
    {{decode:alert-delivery-status:a.delivery_status_code}} AS delivery_status,
    a.delivery_status_code,
    a.title,
    a.message,
    a.channel_name,
    a.deduplication_key,
    a.dedupe_window_start_utc,
    a.occurrence_count,
    a.resolved_at_utc,
    a.retry_count,
    a.retry_after_utc,
    a.created_at_utc,
    a.modified_at_utc,
    a.version
FROM {{schema}}.alerts AS a
JOIN {{schema}}.namespaces AS ns ON ns.id = a.namespace_id
LEFT JOIN {{schema}}.jobs AS j ON j.id = a.job_id
LEFT JOIN {{schema}}.definitions AS d ON d.id = j.definition_id
