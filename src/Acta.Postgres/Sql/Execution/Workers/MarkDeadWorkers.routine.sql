CREATE OR REPLACE FUNCTION {{schema}}.mark_dead_workers(
    p_dead_after_seconds INT
)
RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE
    v_marked INT;
BEGIN
    WITH doomed AS (
        SELECT id
        FROM {{schema}}.workers
        WHERE
            status_code = 10 /* WorkerStatusCode.Active */
            AND last_seen_at_utc < now() - (p_dead_after_seconds * INTERVAL '1 second')
        FOR UPDATE SKIP LOCKED
    ),

    dead AS (
        UPDATE {{schema}}.workers w
        SET
            status_code = 200 /* WorkerStatusCode.Dead */,
            modified_at_utc = now(),
            version = version + 1
        FROM doomed d
        WHERE w.id = d.id
        RETURNING w.id, w.namespace_id
    ),

    evt AS (
        INSERT INTO {{schema}}.events (
            event_code,
            created_at_utc,
            namespace_id,
            actor_code,
            actor_key,
            job_id,
            execution_number,
            lineage_root_id,
            definition_id,
            worker_id,
            from_status_code,
            to_status_code,
            execution_status_code,
            duration_ms,
            reason_code,
            reason_message)
        SELECT
            122 /* JobEventCode.WorkerDead */,
            now(),
            d.namespace_id,
            70 /* JobActorCode.Worker */,
            d.id::VARCHAR,
            NULL,
            NULL,
            NULL,
            NULL,
            d.id,
            NULL,
            NULL,
            NULL,
            NULL,
            101 /* JobEventReasonCode.WorkerHeartbeatStale */,
            NULL
        FROM dead d
        RETURNING 1
    )

    SELECT count(*) INTO v_marked FROM dead;
    RETURN v_marked;
END;
$$;
