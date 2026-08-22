CREATE OR REPLACE FUNCTION {{schema}}.stop_worker(
    p_namespace_id INT,
    p_worker_id INT
)
RETURNS VOID
LANGUAGE sql
AS $$
    WITH stopped AS (
        UPDATE {{schema}}.workers
        SET
            status_code = 100 /* WorkerStatusCode.Stopped */,
            modified_at_utc = now(),
            version = version + 1
        WHERE
            id = p_worker_id
            AND status_code IN (10 /* WorkerStatusCode.Active */, 80 /* WorkerStatusCode.Draining */)
        RETURNING id, worker_ref
    )
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
        121 /* EventCode.WorkerStopped */,
        now(),
        p_namespace_id,
        70 /* ActorCode.Worker */,
        s.worker_ref::TEXT,
        NULL,
        NULL,
        NULL,
        NULL,
        s.id,
        NULL,
        NULL,
        NULL,
        NULL,
        100 /* JobEventReasonCode.WorkerCleanShutdown */,
        NULL
    FROM stopped s;
$$;
