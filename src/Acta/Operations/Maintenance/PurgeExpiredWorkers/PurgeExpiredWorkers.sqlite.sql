-- SQLite owns one immediate transaction for the file.
DROP TABLE IF EXISTS temp._purge_workers;

-- Events about a terminal worker land after its last heartbeat, and each one stamps modified_at_utc, so the row goes
-- once both are past the window and no Executing job it leases is left for recovery to reclaim in its name.
CREATE TEMP TABLE _purge_workers AS
SELECT w.worker_id
FROM {{schema}}.workers w
WHERE
    w.namespace_id = @p_namespace_id
    AND w.worker_status_code IN (100 /* WorkerStatusCode.Stopped */, 200 /* WorkerStatusCode.Dead */)
    AND w.last_seen_at_utc <= @p_cutoff_utc
    AND w.modified_at_utc <= @p_cutoff_utc
    AND NOT EXISTS (
        SELECT 1
        FROM {{schema}}.jobs_state r
        WHERE
            r.leased_by_worker_id = w.worker_id
            -- ix_jobs_state_worker_inflight's filter restated: a partial index serves only a query that states it.
            AND r.leased_by_worker_id IS NOT NULL
            AND r.job_status_code = 50 /* JobStatusCode.Executing */
    )
ORDER BY w.last_seen_at_utc, w.worker_id
LIMIT @p_batch_size;

DELETE FROM {{schema}}.workers WHERE worker_id IN (SELECT p.worker_id FROM temp._purge_workers p);

SELECT COUNT(*) AS deleted_count FROM temp._purge_workers;
