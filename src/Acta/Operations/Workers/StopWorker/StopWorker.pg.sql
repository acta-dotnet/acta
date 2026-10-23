WITH stopped AS (
    UPDATE {{schema}}.workers
    SET
        worker_status_code = 100 /* WorkerStatusCode.Stopped */,
        modified_at_utc = now(),
        version = version + 1
    WHERE
        worker_id = @p_worker_id
        AND worker_status_code IN (10 /* WorkerStatusCode.Active */, 80 /* WorkerStatusCode.Draining */)
    RETURNING worker_id
)
INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    worker_id,
    entity_code,
    entity_id)
SELECT
    121 /* EventCode.WorkerStopped */,
    now(),
    @p_namespace_id,
    s.worker_id,
    130 /* EntityCode.Worker */,
    s.worker_id
FROM stopped s;
