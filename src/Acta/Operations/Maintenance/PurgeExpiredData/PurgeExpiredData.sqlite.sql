-- Only the selected section can stage rows; SQLite owns one immediate transaction for the file.
DROP TABLE IF EXISTS temp._purge_slice;
DROP TABLE IF EXISTS temp._purge_jobs;
DROP TABLE IF EXISTS temp._purge_events;
DROP TABLE IF EXISTS temp._purge_alerts;
DROP TABLE IF EXISTS temp._purge_undelivered_alerts;
DROP TABLE IF EXISTS temp._purge_workers;
DROP TABLE IF EXISTS temp._purge_locks;
DROP TABLE IF EXISTS temp._purge_lanes;

-- No retention index: walk jobs_state downward from the cursor, ten batches a slice, and delete this namespace's
-- expired jobs in it; downward, so a walk has a fixed end however fast enqueues arrive. A full batch ends the
-- slice at its last delete; the oldest row answers zero, and the walk restarts.
CREATE TEMP TABLE _purge_slice AS
SELECT
    CASE WHEN @p_before_job_id = 0 THEN 9223372036854775807 ELSE @p_before_job_id END AS below,
    (
        SELECT MIN(s.job_id)
        FROM (
            SELECT r.job_id
            FROM {{schema}}.jobs_state r
            WHERE @p_section = 1 AND r.job_id < CASE WHEN @p_before_job_id = 0 THEN 9223372036854775807 ELSE @p_before_job_id END
            ORDER BY r.job_id DESC
            LIMIT @p_batch_size * 10
        ) s
    ) AS slice_end;

CREATE TEMP TABLE _purge_jobs AS
SELECT r.job_id
FROM {{schema}}.jobs_state r
JOIN {{schema}}.jobs j ON j.job_id = r.job_id
WHERE
    @p_section = 1
    AND r.job_id < (SELECT below FROM temp._purge_slice)
    AND r.job_id >= (SELECT slice_end FROM temp._purge_slice)
    AND r.namespace_id = @p_namespace_id
    AND r.job_status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
    AND r.retention_until_utc IS NOT NULL
    AND r.retention_until_utc <= @p_cutoff_utc
    -- Lineage guard: parent_job_id carries no FK, so purging a parent whose children still exist would
    -- orphan their lineage (same rule as the manual purge_job). Only leaves delete in this pass; a
    -- fully-expired subtree drains bottom-up across successive batches.
    AND NOT EXISTS (SELECT 1 FROM {{schema}}.jobs c WHERE c.parent_job_id = r.job_id)
    -- A completed child of a live parent is kept: the parent's replay dedupes onto its row and reads
    -- its result, so purging it would run the child again. The tree drains once the parent is terminal.
    AND NOT EXISTS (
        SELECT 1 FROM {{schema}}.jobs_state p
        WHERE p.job_id = j.parent_job_id
          AND p.job_status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */))
    -- A stopped recurring job stays while its definition is live: the next start would register its
    -- declaration as a new job and undo the cancel or failure. Retiring the definition lets it drain.
    AND NOT EXISTS (
        SELECT 1 FROM {{schema}}.schedules s
        JOIN {{schema}}.definitions d ON d.definition_id = j.definition_id
        WHERE s.job_id = j.job_id AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */)
    -- A job stays while the projector still owes one of its events, since raising an alert reads the job row: an event
    -- the projector reads, of an alert-enabled definition, past the floor (the cutoff when none is bound).
    AND NOT EXISTS (
        SELECT 1
        FROM {{schema}}.events e
        JOIN {{schema}}.definitions d ON d.definition_id = e.definition_id
        WHERE
            e.entity_code = 50 /* EntityCode.Job */
            AND e.entity_id = j.job_id
            AND e.created_at_utc >= COALESCE(@p_alert_floor_utc, @p_cutoff_utc)
            AND (
                e.created_at_utc > COALESCE(@p_alert_floor_utc, @p_cutoff_utc)
                OR e.event_id > COALESCE(@p_alert_floor_event_id, 9223372036854775807)
            )
            AND (
                e.event_code IN (41 /* EventCode.JobExecutionFailed */, 42 /* EventCode.JobExecutionOrphaned */, 44 /* EventCode.JobFailed */)
                OR (e.event_code = 40 /* EventCode.JobExecutionSucceeded */ AND e.execution_number > 1)
            )
            AND d.alert_profile_code_effective <> 0 /* AlertProfileCode.None */)
ORDER BY r.job_id DESC
LIMIT @p_batch_size;

DELETE FROM {{schema}}.tags WHERE entity_code = 50 /* EntityCode.Job */ AND entity_id IN (SELECT p.job_id FROM temp._purge_jobs p);

DELETE FROM {{schema}}.checkpoints WHERE job_id IN (SELECT p.job_id FROM temp._purge_jobs p);
DELETE FROM {{schema}}.steps WHERE job_id IN (SELECT p.job_id FROM temp._purge_jobs p);
DELETE FROM {{schema}}.schedules WHERE job_id IN (SELECT p.job_id FROM temp._purge_jobs p);
DELETE FROM {{schema}}.schedules_state WHERE job_id IN (SELECT p.job_id FROM temp._purge_jobs p);
DELETE FROM {{schema}}.jobs WHERE job_id IN (SELECT p.job_id FROM temp._purge_jobs p);
DELETE FROM {{schema}}.jobs_state WHERE job_id IN (SELECT p.job_id FROM temp._purge_jobs p);

-- Events still owed to the alert projector stay past the cutoff, as the jobs section above keeps their jobs.
CREATE TEMP TABLE _purge_events AS
SELECT e.event_id
FROM {{schema}}.events e
WHERE
    @p_section = 2
    AND e.namespace_id = @p_namespace_id
    AND e.created_at_utc <= @p_cutoff_utc
    AND NOT (
        e.entity_code = 50 /* EntityCode.Job */
        AND e.created_at_utc >= COALESCE(@p_alert_floor_utc, @p_cutoff_utc)
        AND (
            e.created_at_utc > COALESCE(@p_alert_floor_utc, @p_cutoff_utc)
            OR e.event_id > COALESCE(@p_alert_floor_event_id, 9223372036854775807)
        )
        AND (
            e.event_code IN (41 /* EventCode.JobExecutionFailed */, 42 /* EventCode.JobExecutionOrphaned */, 44 /* EventCode.JobFailed */)
            OR (e.event_code = 40 /* EventCode.JobExecutionSucceeded */ AND e.execution_number > 1)
        )
        AND EXISTS (
            SELECT 1
            FROM {{schema}}.definitions d
            WHERE d.definition_id = e.definition_id AND d.alert_profile_code_effective <> 0 /* AlertProfileCode.None */))
ORDER BY e.created_at_utc, e.event_id
LIMIT @p_batch_size;

DELETE FROM {{schema}}.events WHERE event_id IN (SELECT p.event_id FROM temp._purge_events p);

CREATE TEMP TABLE _purge_alerts AS
SELECT alert_id
FROM {{schema}}.alerts
WHERE
    @p_section = 3
    AND namespace_id = @p_namespace_id
    AND created_at_utc <= @p_cutoff_utc
    AND delivery_status_code IN (30 /* AlertDeliveryStatusCode.Suppressed */, 100 /* AlertDeliveryStatusCode.Delivered */, 200 /* AlertDeliveryStatusCode.Failed */)
ORDER BY created_at_utc, alert_id
LIMIT @p_batch_size;

DELETE FROM {{schema}}.alerts WHERE alert_id IN (SELECT p.alert_id FROM temp._purge_alerts p);

-- The hard cap: past the window an alert goes whether delivery settled or not, so a stuck row is not
-- immortal. Being an open incident is no shield - ux_alerts_deduplication_key covers unresolved rows only, so
-- the delete frees the identity and the next failure opens a fresh incident.
CREATE TEMP TABLE _purge_undelivered_alerts AS
SELECT alert_id
FROM {{schema}}.alerts
WHERE
    @p_section = 4
    AND namespace_id = @p_namespace_id
    AND created_at_utc <= @p_cutoff_utc
    AND delivery_status_code IN (10 /* AlertDeliveryStatusCode.Pending */, 20 /* AlertDeliveryStatusCode.RetryAfter */)
ORDER BY created_at_utc, alert_id
LIMIT @p_batch_size;

DELETE FROM {{schema}}.alerts WHERE alert_id IN (SELECT p.alert_id FROM temp._purge_undelivered_alerts p);

-- Events about a terminal worker land after its last heartbeat, and each one stamps modified_at_utc, so the row goes
-- once both are past the window and no Executing job it leases is left for recovery to reclaim in its name.
CREATE TEMP TABLE _purge_workers AS
SELECT w.worker_id
FROM {{schema}}.workers w
WHERE
    @p_section = 5
    AND w.namespace_id = @p_namespace_id
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

CREATE TEMP TABLE _purge_locks AS
SELECT lock_key
FROM {{schema}}.locks
WHERE
    @p_section = 6
    AND expires_at_utc <= @p_cutoff_utc
ORDER BY expires_at_utc
LIMIT @p_batch_size;

DELETE FROM {{schema}}.locks WHERE lock_key IN (SELECT lock_key FROM temp._purge_locks);

-- Unreferenced lanes; the immediate transaction is the lanes' mutex on SQLite.
CREATE TEMP TABLE _purge_lanes AS
SELECT l.lane_id
FROM {{schema}}.lanes l
WHERE
    @p_section = 7
    AND l.namespace_id = @p_namespace_id
    AND NOT EXISTS (SELECT 1 FROM {{schema}}.jobs_state r WHERE r.lane_id = l.lane_id)
ORDER BY l.lane_id
LIMIT @p_batch_size;

DELETE FROM {{schema}}.lanes WHERE lane_id IN (SELECT p.lane_id FROM temp._purge_lanes p);

SELECT
    (SELECT COUNT(*) FROM temp._purge_jobs)
    + (SELECT COUNT(*) FROM temp._purge_events)
    + (SELECT COUNT(*) FROM temp._purge_alerts)
    + (SELECT COUNT(*) FROM temp._purge_undelivered_alerts)
    + (SELECT COUNT(*) FROM temp._purge_workers)
    + (SELECT COUNT(*) FROM temp._purge_locks)
    + (SELECT COUNT(*) FROM temp._purge_lanes) AS deleted_count,
    CASE
        WHEN @p_section <> 1 OR (SELECT slice_end FROM temp._purge_slice) IS NULL THEN 0
        WHEN (SELECT COUNT(*) FROM temp._purge_jobs) = @p_batch_size THEN (SELECT MIN(job_id) FROM temp._purge_jobs)
        ELSE (SELECT slice_end FROM temp._purge_slice)
    END AS next_job_id;
