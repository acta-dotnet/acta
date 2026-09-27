CREATE OR REPLACE FUNCTION {{schema}}.resolve_job_alerts(
    p_namespace_id INT,
    p_job_id BIGINT,
    p_source_event_id BIGINT
)
RETURNS TABLE (resolved_count INT)
LANGUAGE plpgsql
AS $$
BEGIN
    -- The job row first, then its alert rows (docs/internals/sql-execution-policy.md, "Alert lock order"):
    -- purge_job holds the row FOR UPDATE while it deletes the job's alerts in id order.
    PERFORM 1 FROM {{schema}}.jobs j WHERE j.id = p_job_id FOR KEY SHARE;

    RETURN QUERY
    WITH resolved AS (
        UPDATE {{schema}}.alerts a
        SET
            resolved_at_utc = now(),
            last_projected_event_id = p_source_event_id,
            /* Closing the incident settles its delivery too: a notification queued for a condition that has
               cleared is cancelled rather than sent, which is what Suppressed already means. An already-settled
               row keeps its status: it records what actually happened to the send, and a resolve does not edit it. */
            delivery_status_code = CASE
                WHEN a.delivery_status_code IN (10 /* AlertDeliveryStatusCode.Pending */, 20 /* AlertDeliveryStatusCode.RetryAfter */)
                    THEN 30 /* AlertDeliveryStatusCode.Suppressed */
                ELSE a.delivery_status_code
            END,
            retry_after_utc = NULL,
            modified_at_utc = now(),
            version = a.version + 1
        WHERE
            a.namespace_id = p_namespace_id
            AND a.job_id = p_job_id
            AND a.origin_code = 10 /* AlertOriginCode.Automatic */
            AND a.kind_code IN (
                10 /* AlertKindCode.FirstFailure */, 20 /* AlertKindCode.ThresholdReached */, 30 /* AlertKindCode.FinalFailure */
            )
            AND a.resolved_at_utc IS NULL
            AND (a.last_projected_event_id IS NULL OR a.last_projected_event_id < p_source_event_id)
        RETURNING 1
    )
    SELECT count(*)::INT FROM resolved;
END;
$$;
