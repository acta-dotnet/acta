SELECT
    a.alert_id,
    a.alert_ref,
    ns.namespace_name AS namespace,
    a.job_id,
    a.job_ref,
    j.definition_id,
    d.definition_name AS job_name,
    {{decode:alert-origin:a.alert_origin_code}} AS origin,
    a.alert_origin_code,
    {{decode:alert-severity:a.alert_severity_code}} AS severity,
    a.alert_severity_code,
    {{decode:alert-kind:a.alert_kind_code}} AS kind,
    a.alert_kind_code,
    {{decode:alert-delivery-status:a.delivery_status_code}} AS delivery_status,
    a.delivery_status_code,
    a.title,
    a.alert_message,
    a.channel_name,
    a.deduplication_key,
    a.occurrence_count,
    a.resolved_at_utc,
    a.acknowledged_at_utc,
    a.retry_count,
    a.retry_after_utc,
    a.created_at_utc,
    a.modified_at_utc,
    a.version
FROM {{schema}}.alerts AS a
JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = a.namespace_id
LEFT JOIN {{schema}}.jobs AS j ON j.job_id = a.job_id
LEFT JOIN {{schema}}.definitions AS d ON d.definition_id = j.definition_id
