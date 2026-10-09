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

-- The answer is the last result set, so the rows to renew are captured before the update.
DROP TABLE IF EXISTS temp._ewl_renewed;

/* Push the held executions' leases forward. Only a row this worker still leases at the execution the process
   holds is answered, so a row the process lost lapses and recovery takes it. Deliberately no version bump: a
   lease refresh is not a claim-generation change. */
CREATE TEMP TABLE _ewl_renewed AS
SELECT r.job_id, r.execution_number
FROM {{schema}}.runtimes r
WHERE
    r.leased_by_worker_id = @p_leased_by_worker_id
    AND r.status_code IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */)
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

SELECT n.job_id, n.execution_number, 1 AS renewed, NULL AS definitions_modified_at_utc
FROM temp._ewl_renewed n
UNION ALL
-- The namespace's newest definition change: the worker re-reads its catalog only when it moves.
SELECT
    NULL AS job_id,
    NULL AS execution_number,
    0 AS renewed,
    MAX(d.modified_at_utc) AS definitions_modified_at_utc
FROM {{schema}}.definitions d
WHERE d.namespace_id = (SELECT w.namespace_id FROM {{schema}}.workers w WHERE w.id = @p_leased_by_worker_id);
