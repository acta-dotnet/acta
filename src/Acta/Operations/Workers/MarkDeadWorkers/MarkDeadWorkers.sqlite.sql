DROP TABLE IF EXISTS temp._dead_workers;

CREATE TEMP TABLE _dead_workers AS
SELECT worker_id, namespace_id
FROM {{schema}}.workers
WHERE
    -- A worker that stopped heartbeating mid-drain, killed or past its host's shutdown budget, is as dead as
    -- an active one; nothing else ever moves a Draining row.
    worker_status_code IN (10 /* WorkerStatusCode.Active */, 80 /* WorkerStatusCode.Draining */)
    AND last_seen_at_utc < {{now}} - (@p_dead_after_seconds) * 1000;

INSERT INTO {{schema}}.events (
    event_code,
    namespace_id,
    worker_id,
    entity_code,
    entity_id)
SELECT
    122 /* EventCode.WorkerDied */,
    d.namespace_id,
    d.worker_id,
    130 /* EntityCode.Worker */,
    d.worker_id
FROM temp._dead_workers d;

UPDATE {{schema}}.workers
SET
    worker_status_code = 200 /* WorkerStatusCode.Dead */,
    modified_at_utc = {{now}},
    version = version + 1
WHERE worker_id IN (SELECT worker_id FROM temp._dead_workers);

SELECT (SELECT COUNT(*) FROM temp._dead_workers) AS marked;
