DROP TABLE IF EXISTS temp._dead_workers;

CREATE TEMP TABLE _dead_workers AS
SELECT id, namespace_id
FROM {{schema}}.workers
WHERE
    status_code = 10 /* WorkerStatusCode.Active */
    AND last_seen_at_utc < {{now}} - (@p_dead_after_seconds) * 1000;

INSERT INTO {{schema}}.events (
    event_code,
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
    122 /* EventCode.WorkerDied */,
    d.namespace_id,
    70 /* ActorCode.Worker */,
    CAST(d.id AS TEXT),
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
FROM temp._dead_workers d;

UPDATE {{schema}}.workers
SET
    status_code = 200 /* WorkerStatusCode.Dead */,
    modified_at_utc = {{now}},
    version = version + 1
WHERE id IN (SELECT id FROM temp._dead_workers);

SELECT (SELECT COUNT(*) FROM temp._dead_workers);
