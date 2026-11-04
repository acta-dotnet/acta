SELECT TOP (@p_alert_batch_size)
    a.alert_id,
    a.alert_severity_code AS severity,
    a.alert_kind_code AS kind,
    a.title,
    a.alert_message AS message,
    jd.runbook_url_effective AS runbook_url,
    a.occurrence_count,
    a.created_at_utc,
    a.retry_count,
    a.channel_name,
    a.alert_ref,
    a.job_ref,
    a.version,
    a.alert_origin_code AS origin
FROM {{schema}}.alerts a
LEFT JOIN {{schema}}.jobs j
    ON j.job_id = a.job_id
LEFT JOIN {{schema}}.definitions jd
    ON jd.definition_id = j.definition_id
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
        /* Arm 2 - reminder. Delivery settled, the incident did not, and the instant that settlement
           scheduled has come. Failed is included so a send that failed cannot silence an open incident;
           Suppressed is not, because re-sending would only re-take the channel's routing decision. */
        OR (
            a.delivery_status_code IN (100 /* AlertDeliveryStatusCode.Delivered */, 200 /* AlertDeliveryStatusCode.Failed */)
            AND a.retry_after_utc IS NOT NULL
            AND a.retry_after_utc <= SYSUTCDATETIME()
        )
    )
ORDER BY a.alert_id;
