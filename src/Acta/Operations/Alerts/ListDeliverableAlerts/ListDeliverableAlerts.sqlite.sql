SELECT
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
    a.version
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
            AND (a.retry_after_utc IS NULL OR a.retry_after_utc <= {{now}})
        )
        /* Arm 2 - reminder. Delivery settled, the incident did not, and the instant that settlement
           scheduled has come. Failed is included so a send that failed cannot silence an open incident;
           Suppressed is not, because re-sending would only re-take the channel's routing decision. */
        OR (
            a.delivery_status_code IN (100 /* AlertDeliveryStatusCode.Delivered */, 200 /* AlertDeliveryStatusCode.Failed */)
            AND a.retry_after_utc IS NOT NULL
            AND a.retry_after_utc <= {{now}}
        )
    )
    /* A recovery past the projector's cursor holds the automatic alert it will resolve: the job's retry success, read
       as the projector reads it, under a newer id than the alert's mark. The projection lags by its horizon, so without
       this a failure that healed inside that lag is sent before the pass that reaches its recovery closes it. */
    AND NOT EXISTS (
        SELECT 1
        FROM {{schema}}.events s
        WHERE
            a.alert_kind_code IN (
                10 /* AlertKindCode.FirstFailure */, 20 /* AlertKindCode.ThresholdReached */, 30 /* AlertKindCode.FinalFailure */
            )
            AND s.entity_code = 50 /* EntityCode.Job */
            AND s.entity_id = a.job_id
            AND s.created_at_utc >= @p_cursor_utc
            AND (s.created_at_utc > @p_cursor_utc OR s.event_id > @p_cursor_event_id)
            AND s.event_code = 40 /* EventCode.JobExecutionSucceeded */
            AND s.execution_number > 1
            AND s.event_id > a.last_projected_event_id
    )
ORDER BY a.alert_id
LIMIT @p_alert_batch_size;
