WITH gone AS (
    SELECT g.worker_id::int AS worker_id
    FROM json_array_elements_text(@p_worker_ids::json) AS g (worker_id)
),

-- A released worker still Active or Draining turns Dead, as the dead-worker sweep would turn it.
dead AS (
    UPDATE {{schema}}.workers w
    SET
        worker_status_code = 200 /* WorkerStatusCode.Dead */,
        modified_at_utc = now(),
        version = version + 1
    FROM gone g
    WHERE
        w.worker_id = g.worker_id
        AND w.worker_status_code IN (10 /* WorkerStatusCode.Active */, 80 /* WorkerStatusCode.Draining */)
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
),

-- The holds its executing jobs took, a RunWithLockAsync lock or a concurrency slot, free now; a rate meter's rows
-- hold nothing and stay.
held AS (
    UPDATE {{schema}}.locks k
    SET expires_at_utc = now()
    FROM {{schema}}.jobs_state r
    WHERE
        r.job_id = k.job_id
        AND r.leased_by_worker_id IN (SELECT g.worker_id FROM gone g)
        AND r.job_status_code = 50 /* JobStatusCode.Executing */
        AND k.expires_at_utc > now()
        AND (k.lock_key LIKE r.namespace_id || '.lock.%' OR k.lock_key LIKE r.namespace_id || '.sem.%')
    RETURNING 1
)

-- Its leases lapse now, so the recovery pass that follows reclaims the jobs. A lease write bumps no version.
UPDATE {{schema}}.jobs_state
SET lease_expires_at_utc = now() - INTERVAL '1 millisecond'
WHERE
    leased_by_worker_id IN (SELECT g.worker_id FROM gone g)
    AND job_status_code = 50 /* JobStatusCode.Executing */
    AND lease_expires_at_utc >= now();
