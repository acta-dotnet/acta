-- A released worker still Active or Draining turns Dead, as the dead-worker sweep would turn it. The event is written
-- before the update replaces the status it reads.
INSERT INTO {{schema}}.events (
    event_code,
    namespace_id,
    worker_id,
    entity_code,
    entity_id)
SELECT
    46 /* EventCode.WorkerDied */,
    w.namespace_id,
    w.worker_id,
    7 /* EntityCode.Worker */,
    w.worker_id
FROM {{schema}}.workers w
WHERE
    w.worker_id IN (SELECT g.value FROM json_each(@p_worker_ids) g)
    AND w.worker_status_code IN (10 /* WorkerStatusCode.Active */, 80 /* WorkerStatusCode.Draining */);

UPDATE {{schema}}.workers
SET
    worker_status_code = 200 /* WorkerStatusCode.Dead */,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    worker_id IN (SELECT g.value FROM json_each(@p_worker_ids) g)
    AND worker_status_code IN (10 /* WorkerStatusCode.Active */, 80 /* WorkerStatusCode.Draining */);

-- The holds its executing jobs took, a RunWithLockAsync lock or a concurrency slot, free now; a rate meter's rows
-- hold nothing and stay.
UPDATE {{schema}}.locks
SET expires_at_utc = {{now}}
WHERE
    expires_at_utc > {{now}}
    AND EXISTS (
        SELECT 1
        FROM {{schema}}.jobs_state r
        WHERE
            r.job_id = locks.job_id
            AND r.leased_by_worker_id IN (SELECT g.value FROM json_each(@p_worker_ids) g)
            AND r.job_status_code = 50 /* JobStatusCode.Executing */
            AND (locks.lock_key LIKE r.namespace_id || '.lock.%' OR locks.lock_key LIKE r.namespace_id || '.sem.%')
    );

-- Its leases lapse now, so the recovery pass that follows reclaims the jobs. A lease write bumps no version.
UPDATE {{schema}}.jobs_state
SET lease_expires_at_utc = {{now}} - 1
WHERE
    leased_by_worker_id IN (SELECT g.value FROM json_each(@p_worker_ids) g)
    AND job_status_code = 50 /* JobStatusCode.Executing */
    AND lease_expires_at_utc >= {{now}};
