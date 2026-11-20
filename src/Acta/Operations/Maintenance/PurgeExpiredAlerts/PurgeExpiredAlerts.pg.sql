-- Past the window an alert goes whether its delivery settled or not, so a stuck row is not immortal, and an open
-- incident goes too, which frees its identity for the next failure. A row a raise holds is passed. Materialized, so
-- the locking read runs once: run again per row, it would lock other rows and delete past the batch.
WITH alert_del AS MATERIALIZED (
    SELECT a.alert_id
    FROM {{schema}}.alerts a
    WHERE
        a.namespace_id = @p_namespace_id
        AND a.created_at_utc <= @p_cutoff_utc
    ORDER BY a.created_at_utc, a.alert_id
    LIMIT @p_batch_size
    FOR UPDATE SKIP LOCKED
),
deleted AS (
    DELETE FROM {{schema}}.alerts a
    USING alert_del d
    WHERE a.alert_id = d.alert_id
    RETURNING a.delivery_status_code
)
SELECT
    count(*)::INT AS deleted_count,
    count(*) FILTER (
        WHERE delivery_status_code IN (10 /* AlertDeliveryStatusCode.Pending */, 20 /* AlertDeliveryStatusCode.Retrying */)
    )::INT AS undelivered_count
FROM deleted;
