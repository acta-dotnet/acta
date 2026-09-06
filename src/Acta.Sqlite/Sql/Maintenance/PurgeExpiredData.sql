-- Only the selected section can stage rows; SQLite owns one immediate transaction for the file.
DROP TABLE IF EXISTS temp._purge_skip;
DROP TABLE IF EXISTS temp._purge_jobs;
DROP TABLE IF EXISTS temp._purge_events;
DROP TABLE IF EXISTS temp._purge_alerts;
DROP TABLE IF EXISTS temp._purge_undelivered_alerts;
DROP TABLE IF EXISTS temp._purge_workers;
DROP TABLE IF EXISTS temp._purge_locks;

CREATE TEMP TABLE _purge_jobs AS
SELECT r.job_id AS id
FROM {{schema}}.runtimes r
WHERE
    @p_section = 1
    AND r.namespace_id = @p_namespace_id
    AND r.status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
    AND r.retention_until_utc IS NOT NULL
    AND r.retention_until_utc <= @p_cutoff_utc
    -- Lineage guard: parent_id carries no FK, so purging a parent whose children still exist would
    -- orphan their lineage (same rule as the manual purge_job). Only leaves delete in this pass; a
    -- fully-expired subtree drains bottom-up across successive batches.
    AND NOT EXISTS (SELECT 1 FROM {{schema}}.jobs c WHERE c.parent_id = r.job_id)
ORDER BY r.retention_until_utc, r.job_id
LIMIT @p_batch_size;

DELETE FROM {{schema}}.tags
WHERE
    (scope_code = 50 /* TagScopeCode.Job */ AND scope_id IN (SELECT id FROM temp._purge_jobs))
    OR (scope_code = 60 /* TagScopeCode.Schedule */ AND scope_id IN (
        SELECT s.id FROM {{schema}}.schedules s WHERE s.job_id IN (SELECT id FROM temp._purge_jobs)));

DELETE FROM {{schema}}.jobs WHERE id IN (SELECT id FROM temp._purge_jobs);

CREATE TEMP TABLE _purge_events AS
SELECT id
FROM {{schema}}.events
WHERE
    @p_section = 2
    AND namespace_id = @p_namespace_id
    AND created_at_utc <= @p_cutoff_utc
ORDER BY created_at_utc, id
LIMIT @p_batch_size;

DELETE FROM {{schema}}.tags WHERE scope_code = 90 /* TagScopeCode.Event */ AND scope_id IN (SELECT id FROM temp._purge_events);
DELETE FROM {{schema}}.events WHERE id IN (SELECT id FROM temp._purge_events);

CREATE TEMP TABLE _purge_alerts AS
SELECT id
FROM {{schema}}.alerts
WHERE
    @p_section = 3
    AND namespace_id = @p_namespace_id
    AND created_at_utc <= @p_cutoff_utc
    AND delivery_status_code IN (30 /* AlertDeliveryStatusCode.Suppressed */, 100 /* AlertDeliveryStatusCode.Delivered */, 200 /* AlertDeliveryStatusCode.Failed */)
ORDER BY created_at_utc, id
LIMIT @p_batch_size;

DELETE FROM {{schema}}.tags WHERE scope_code = 80 /* TagScopeCode.Alert */ AND scope_id IN (SELECT id FROM temp._purge_alerts);
DELETE FROM {{schema}}.alerts WHERE id IN (SELECT id FROM temp._purge_alerts);

-- The hard cap: past the window an alert goes whether delivery settled or not, so a stuck row is not
-- immortal. Being an open incident is no shield - ux_alerts_dedupe covers unresolved rows only, so
-- the delete frees the identity and the next failure opens a fresh incident.
CREATE TEMP TABLE _purge_undelivered_alerts AS
SELECT id
FROM {{schema}}.alerts
WHERE
    @p_section = 4
    AND namespace_id = @p_namespace_id
    AND created_at_utc <= @p_cutoff_utc
    AND delivery_status_code IN (10 /* AlertDeliveryStatusCode.Pending */, 20 /* AlertDeliveryStatusCode.RetryAfter */)
ORDER BY created_at_utc, id
LIMIT @p_batch_size;

DELETE FROM {{schema}}.tags
WHERE scope_code = 80 /* TagScopeCode.Alert */ AND scope_id IN (SELECT id FROM temp._purge_undelivered_alerts);
DELETE FROM {{schema}}.alerts WHERE id IN (SELECT id FROM temp._purge_undelivered_alerts);

-- sys.alerts records one poison-skip variable per unprojectable event on its own recurring slot
-- (whose deduplication_key is the job name) and never reads it back; nothing else prunes them, so
-- they age out on the alert window like the alerts they stand in for.
CREATE TEMP TABLE _purge_skip AS
    SELECT c.rowid AS id
    FROM {{schema}}.checkpoints c
    INNER JOIN {{schema}}.jobs j ON j.id = c.job_id
    WHERE
        @p_section = 5
        AND j.namespace_id = @p_namespace_id
        AND j.deduplication_key = 'sys.alerts'
        AND j.parent_id IS NULL
        AND c.kind_code = 10 /* JobCheckpointKindCode.Variable */
        AND c.name LIKE 'alerts-skip-%'
        AND c.modified_at_utc <= @p_cutoff_utc
    LIMIT @p_batch_size;

DELETE FROM {{schema}}.checkpoints WHERE rowid IN (SELECT id FROM temp._purge_skip);

CREATE TEMP TABLE _purge_workers AS
SELECT id
FROM {{schema}}.workers
WHERE
    @p_section = 6
    AND namespace_id = @p_namespace_id
    AND status_code IN (100 /* WorkerStatusCode.Stopped */, 200 /* WorkerStatusCode.Dead */)
    AND last_seen_at_utc <= @p_cutoff_utc
ORDER BY last_seen_at_utc, id
LIMIT @p_batch_size;

DELETE FROM {{schema}}.tags WHERE scope_code = 70 /* TagScopeCode.Worker */ AND scope_id IN (SELECT id FROM temp._purge_workers);
DELETE FROM {{schema}}.workers WHERE id IN (SELECT id FROM temp._purge_workers);

CREATE TEMP TABLE _purge_locks AS
SELECT lock_key
FROM {{schema}}.locks
WHERE
    @p_section = 7
    AND expires_at_utc <= @p_cutoff_utc
ORDER BY expires_at_utc
LIMIT @p_batch_size;

DELETE FROM {{schema}}.locks WHERE lock_key IN (SELECT lock_key FROM temp._purge_locks);

SELECT
    (SELECT COUNT(*) FROM temp._purge_jobs)
    + (SELECT COUNT(*) FROM temp._purge_events)
    + (SELECT COUNT(*) FROM temp._purge_alerts)
    + (SELECT COUNT(*) FROM temp._purge_undelivered_alerts)
    + (SELECT COUNT(*) FROM temp._purge_skip)
    + (SELECT COUNT(*) FROM temp._purge_workers)
    + (SELECT COUNT(*) FROM temp._purge_locks) AS deleted_count;
