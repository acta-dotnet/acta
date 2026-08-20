UPDATE {{schema}}.alerts
SET
    resolved_at_utc = SYSUTCDATETIME(),
    last_projected_event_id = @p_source_event_id,
    /* Closing the incident settles its delivery too: a notification queued for a condition that has
       cleared is cancelled rather than sent, which is what Suppressed already means. An already-settled
       row keeps its status: it records what actually happened to the send, and a resolve does not edit it. */
    delivery_status_code = CASE
        WHEN delivery_status_code IN (10 /* AlertDeliveryStatusCode.Pending */, 20 /* AlertDeliveryStatusCode.RetryAfter */)
            THEN 30 /* AlertDeliveryStatusCode.Suppressed */
        ELSE delivery_status_code
    END,
    retry_after_utc = NULL,
    modified_at_utc = SYSUTCDATETIME(),
    version = version + 1
WHERE
    namespace_id = @p_namespace_id
    AND job_id = @p_job_id
    AND origin_code = 10 /* AlertOriginCode.Automatic */
    AND kind_code IN (10 /* AlertKindCode.FirstFailure */, 20 /* AlertKindCode.ThresholdReached */, 30 /* AlertKindCode.FinalFailure */)
    AND resolved_at_utc IS NULL
    AND (last_projected_event_id IS NULL OR last_projected_event_id < @p_source_event_id);
