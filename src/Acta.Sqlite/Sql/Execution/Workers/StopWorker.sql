UPDATE {{schema}}.workers
SET
    status_code = 100 /* WorkerStatusCode.Stopped */,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    id = @p_worker_id
    AND status_code IN (10 /* WorkerStatusCode.Active */, 80 /* WorkerStatusCode.Draining */);

INSERT INTO {{schema}}.events (
    event_code, namespace_id, actor_code, actor_key, job_id, execution_number,
    lineage_root_id, definition_id, worker_id, from_status_code, to_status_code,
    execution_status_code, duration_ms, reason_code, reason_message
)
SELECT
    121 /* EventCode.WorkerStopped */,
    @p_namespace_id,
    70 /* ActorCode.Worker */,
    w.worker_ref,
    NULL,
    NULL,
    NULL,
    NULL,
    w.id,
    NULL,
    NULL,
    NULL,
    NULL,
    100 /* JobEventReasonCode.WorkerCleanShutdown */,
    NULL
FROM {{schema}}.workers w
WHERE w.id = @p_worker_id AND CHANGES() > 0;
