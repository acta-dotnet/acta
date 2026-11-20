-- A batch reads on from the position its pass gives, so no batch reads the index entries an earlier one deleted, and
-- collects by the primary key, so each delete is a direct seek on pk_events. Materialized, so the locking read runs
-- once: run again per row, it would lock other rows and delete past the batch.
WITH event_del AS MATERIALIZED (
    SELECT e.worker_id, e.event_id, e.created_at_utc
    FROM {{schema}}.events e
    WHERE
        e.namespace_id = @p_namespace_id
        AND e.created_at_utc < @p_cutoff_utc
        AND e.created_at_utc >= @p_after_created_utc
        AND (e.created_at_utc > @p_after_created_utc OR e.event_id > @p_after_event_id)
    ORDER BY e.created_at_utc, e.event_id
    LIMIT @p_batch_size
    FOR UPDATE OF e SKIP LOCKED
),
deleted AS (
    DELETE FROM {{schema}}.events e
    USING event_del d
    WHERE e.worker_id = d.worker_id AND e.event_id = d.event_id
)
SELECT
    (SELECT count(*)::INT FROM event_del) AS deleted_count,
    COALESCE(l.created_at_utc, @p_after_created_utc) AS last_created_utc,
    COALESCE(l.event_id, @p_after_event_id) AS last_event_id
FROM (SELECT 1) one
LEFT JOIN (SELECT d.created_at_utc, d.event_id FROM event_del d ORDER BY d.created_at_utc DESC, d.event_id DESC LIMIT 1) l ON true;
