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

/* Push the held executions' leases forward. Only a row this worker still leases at the execution the process
   holds is answered, so a row the process lost lapses and recovery takes it. Deliberately no version bump: a
   lease refresh is not a claim-generation change. */
UPDATE {{schema}}.runtimes
SET lease_expires_at_utc = {{now}} + (@p_lease_ttl_seconds) * 1000
WHERE
    leased_by_worker_id = @p_leased_by_worker_id
    AND status_code IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */)
    AND EXISTS (
        SELECT 1
        FROM json_each(@p_executions) h
        WHERE
            json_extract(h.value, '$.job_id') = runtimes.job_id
            AND json_extract(h.value, '$.execution_number') = runtimes.execution_number
    )
RETURNING job_id, execution_number, 1 AS renewed;
