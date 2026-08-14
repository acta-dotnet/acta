-- Point-read of one alert row by id, projecting the same columns as ListJobAlerts so both share
-- the row shape. Missing id returns no rows (the caller answers null).
SELECT
    a.id,
    ns.name,
    a.job_id,
    a.origin_code,
    a.severity_code,
    a.kind_code,
    a.title,
    a.message,
    a.channel_name,
    a.occurrence_count,
    a.resolved_at_utc,
    a.delivery_status_code,
    a.retry_count,
    a.retry_after_utc,
    a.created_at_utc,
    a.modified_at_utc,
    a.job_ref,
    a.acknowledged_at_utc
FROM {{schema}}.alerts a
JOIN {{schema}}.namespaces ns ON ns.id = a.namespace_id
WHERE a.id = @p_id;
