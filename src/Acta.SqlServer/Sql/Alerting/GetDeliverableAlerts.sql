SELECT TOP (@p_alert_batch_size)
    a.id,
    a.job_id,
    a.severity_code,
    a.kind_code,
    a.title,
    a.message,
    jd.runbook_url_effective AS runbook_url,
    a.occurrence_count,
    a.created_at_utc,
    a.retry_count,
    a.channel_name
FROM {{schema}}.alerts a
LEFT JOIN {{schema}}.jobs j
    ON j.id = a.job_id
LEFT JOIN {{schema}}.definitions jd
    ON jd.id = j.definition_id
WHERE
    a.namespace_id = @p_namespace_id
    AND a.delivery_status_code IN (10 /* AlertDeliveryStatusCode.Pending */, 20 /* AlertDeliveryStatusCode.RetryAfter */)
    AND (a.retry_after_utc IS NULL OR a.retry_after_utc <= SYSUTCDATETIME())
ORDER BY a.id;
