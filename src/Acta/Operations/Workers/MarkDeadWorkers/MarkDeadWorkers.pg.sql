WITH doomed AS (
    SELECT worker_id
    FROM {{schema}}.workers
    WHERE
        -- A worker that stopped heartbeating mid-drain, killed or past its host's shutdown budget, is as
        -- dead as an active one; nothing else ever moves a Draining row.
        worker_status_code IN (10 /* WorkerStatusCode.Active */, 80 /* WorkerStatusCode.Draining */)
        AND last_seen_at_utc < now() - (@p_dead_after_seconds * INTERVAL '1 second')
    FOR UPDATE SKIP LOCKED
),

dead AS (
    UPDATE {{schema}}.workers w
    SET
        worker_status_code = 200 /* WorkerStatusCode.Dead */,
        modified_at_utc = now(),
        version = version + 1
    FROM doomed d
    WHERE w.worker_id = d.worker_id
    RETURNING w.worker_id, w.namespace_id
),

evt AS (
    INSERT INTO {{schema}}.events (
        event_code,
        created_at_utc,
        namespace_id,
        worker_id,
        entity_code,
        entity_id)
    SELECT
        122 /* EventCode.WorkerDied */,
        now(),
        d.namespace_id,
        d.worker_id,
        130 /* EntityCode.Worker */,
        d.worker_id
    FROM dead d
    RETURNING 1
)
SELECT count(*)::INT AS marked FROM dead;
