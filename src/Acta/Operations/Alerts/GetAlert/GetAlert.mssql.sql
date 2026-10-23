-- Point-read of one alert row by ref, projecting the same columns as ListAlerts so both share
-- the row shape. Missing ref returns no rows (the caller answers null).
SELECT
    a.alert_id,
    ns.namespace_name AS job_namespace,
    a.job_id,
    a.alert_origin_code AS origin,
    a.alert_severity_code AS severity,
    a.alert_kind_code AS kind,
    a.title,
    a.alert_message AS message,
    a.channel_name,
    a.occurrence_count,
    a.resolved_at_utc,
    a.delivery_status_code AS delivery_status,
    a.retry_count,
    a.retry_after_utc,
    a.created_at_utc,
    a.modified_at_utc,
    a.job_ref,
    a.acknowledged_at_utc,
    a.alert_ref
FROM {{schema}}.alerts a
JOIN {{schema}}.namespaces ns ON ns.namespace_id = a.namespace_id
WHERE a.alert_ref = @p_alert_ref;
