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
    a.channel_name,
    a.alert_ref,
    a.job_ref,
    a.version
FROM {{schema}}.alerts a
LEFT JOIN {{schema}}.jobs j
    ON j.id = a.job_id
LEFT JOIN {{schema}}.definitions jd
    ON jd.id = j.definition_id
WHERE
    a.namespace_id = @p_namespace_id
    /* A resolved incident is never delivered again, on either arm: the condition cleared, so the
       notification it was about is stale. */
    AND a.resolved_at_utc IS NULL
    AND (
        /* Arm 1 - first attempt or a due retry. */
        (
            a.delivery_status_code IN (10 /* AlertDeliveryStatusCode.Pending */, 20 /* AlertDeliveryStatusCode.RetryAfter */)
            AND (a.retry_after_utc IS NULL OR a.retry_after_utc <= SYSUTCDATETIME())
        )
        /* Arm 2 - reminder: the incident is still open and its delivery already settled, one way or the
           other, at least an interval ago. Failed is included so a failed send cannot silence an open
           incident forever; Suppressed is not, because re-sending would only re-take a routing decision. */
        OR (
            a.delivery_status_code IN (100 /* AlertDeliveryStatusCode.Delivered */, 200 /* AlertDeliveryStatusCode.Failed */)
            AND a.modified_at_utc <= DATEADD(SECOND, -@p_alert_reminder_seconds, SYSUTCDATETIME())
        )
    )
ORDER BY a.id;
