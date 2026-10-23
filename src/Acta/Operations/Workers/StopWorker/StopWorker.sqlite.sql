UPDATE {{schema}}.workers
SET
    worker_status_code = 100 /* WorkerStatusCode.Stopped */,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    worker_id = @p_worker_id
    AND worker_status_code IN (10 /* WorkerStatusCode.Active */, 80 /* WorkerStatusCode.Draining */);

INSERT INTO {{schema}}.events (
    event_code,
    namespace_id,
    worker_id,
    entity_code,
    entity_id)
SELECT
    121 /* EventCode.WorkerStopped */,
    @p_namespace_id,
    w.worker_id,
    130 /* EntityCode.Worker */,
    w.worker_id
FROM {{schema}}.workers w
WHERE w.worker_id = @p_worker_id AND CHANGES() > 0;
