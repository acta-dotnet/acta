UPDATE {{schema}}.workers
SET
    last_seen_at_utc = {{now}},
    -- A worker marked Dead through a database outage longer than its dead-after window is alive again the
    -- moment it heartbeats; Stopped stays Stopped.
    status_code = CASE
        WHEN @p_draining = 1 AND status_code IN (10 /* WorkerStatusCode.Active */, 200 /* WorkerStatusCode.Dead */)
            THEN 80 /* WorkerStatusCode.Draining */
        WHEN status_code = 200 /* WorkerStatusCode.Dead */ THEN 10 /* WorkerStatusCode.Active */
        ELSE status_code
    END,
    modified_at_utc = {{now}},
    version = version + 1
WHERE id = @p_leased_by_worker_id;

-- The answer is the last result set, so the locks and rows to renew are captured before the updates.
DROP TABLE IF EXISTS temp._ewl_locks;
DROP TABLE IF EXISTS temp._ewl_renewed;
DROP TABLE IF EXISTS temp._ewl_recovery;

-- Lock holds renew first: a hold whose token moved is not answered, and the attempt that relied on it stops.
CREATE TEMP TABLE _ewl_locks AS
SELECT k.lock_key
FROM json_each(@p_locks) h
INNER JOIN {{schema}}.locks k
    ON k.lock_key = json_extract(h.value, '$.lock_key') AND k.hold_token = json_extract(h.value, '$.hold_token');

UPDATE {{schema}}.locks
SET expires_at_utc = {{now}} + (@p_lease_ttl_seconds) * 1000
WHERE lock_key IN (SELECT lock_key FROM temp._ewl_locks);

/* Push the held executions' leases forward. Only a row this worker still leases at the execution the process
   holds is answered, so a row the process lost lapses and recovery takes it. Deliberately no version bump: a
   lease refresh is not a claim-generation change. */
CREATE TEMP TABLE _ewl_renewed AS
SELECT r.job_id, r.execution_number
FROM {{schema}}.runtimes r
WHERE
    r.leased_by_worker_id = @p_leased_by_worker_id
    AND r.status_code = 50 /* JobStatusCode.Executing */
    AND EXISTS (
        SELECT 1
        FROM json_each(@p_executions) h
        WHERE
            json_extract(h.value, '$.job_id') = r.job_id
            AND json_extract(h.value, '$.execution_number') = r.execution_number
    );

UPDATE {{schema}}.runtimes
SET lease_expires_at_utc = {{now}} + (@p_lease_ttl_seconds) * 1000
WHERE job_id IN (SELECT job_id FROM temp._ewl_renewed);

-- Takes the namespace's recovery pass when it is due, moving the next one an interval out; this worker runs it.
-- The script holds the one write lock, so reading the due time and moving it cannot interleave with another worker.
CREATE TEMP TABLE _ewl_recovery AS
SELECT n.id
FROM {{schema}}.namespaces n
WHERE
    @p_recovery_interval_seconds IS NOT NULL
    AND n.id = (SELECT w.namespace_id FROM {{schema}}.workers w WHERE w.id = @p_leased_by_worker_id)
    AND (n.recovery_due_at_utc IS NULL OR n.recovery_due_at_utc <= {{now}});

UPDATE {{schema}}.namespaces
SET recovery_due_at_utc = {{now}} + @p_recovery_interval_seconds * 1000
WHERE id IN (SELECT id FROM temp._ewl_recovery);

SELECT
    NULL AS job_id,
    NULL AS execution_number,
    l.lock_key,
    1 AS renewed,
    NULL AS definitions_modified_at_utc,
    0 AS recovery_claimed
FROM temp._ewl_locks l
UNION ALL
SELECT n.job_id, n.execution_number, NULL, 1, NULL, 0
FROM temp._ewl_renewed n
UNION ALL
-- The namespace's newest definition change: the worker re-reads its catalog only when it moves.
SELECT
    NULL AS job_id,
    NULL AS execution_number,
    NULL AS lock_key,
    0 AS renewed,
    MAX(d.modified_at_utc) AS definitions_modified_at_utc,
    (SELECT COUNT(*) FROM temp._ewl_recovery) AS recovery_claimed
FROM {{schema}}.definitions d
WHERE d.namespace_id = (SELECT w.namespace_id FROM {{schema}}.workers w WHERE w.id = @p_leased_by_worker_id);
