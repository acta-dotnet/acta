DROP TABLE IF EXISTS temp._purge_jobs;
DROP TABLE IF EXISTS temp._purge_events;
DROP TABLE IF EXISTS temp._purge_alerts;
DROP TABLE IF EXISTS temp._purge_workers;
DROP TABLE IF EXISTS temp._purge_locks;

CREATE TEMP TABLE _purge_jobs AS
SELECT r.job_id AS id FROM {{schema}}.runtimes r
 WHERE r.namespace_id = @p_namespace_id
   AND r.status_code IN (100 /* JobStatusCode.Done */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
   AND r.retention_until_utc IS NOT NULL
   AND r.retention_until_utc <= {{now}}
   -- Lineage guard: parent_id carries no FK, so purging a parent whose children still exist would
   -- orphan their lineage (same rule as the manual purge_job). Only leaves delete in this pass; a
   -- fully-expired subtree drains bottom-up across successive retention ticks.
   AND NOT EXISTS (SELECT 1 FROM {{schema}}.jobs c WHERE c.parent_id = r.job_id)
 ORDER BY r.retention_until_utc, r.job_id
 LIMIT (@p_batch_size * @p_max_iterations);

DELETE FROM {{schema}}.tags
 WHERE (scope_code = 50 /* TagScopeCode.Job */ AND scope_id IN (SELECT id FROM temp._purge_jobs))
    OR (scope_code = 60 /* TagScopeCode.Schedule */ AND scope_id IN (
        SELECT s.id FROM {{schema}}.schedules s WHERE s.job_id IN (SELECT id FROM temp._purge_jobs)));

DELETE FROM {{schema}}.jobs WHERE id IN (SELECT id FROM temp._purge_jobs);

CREATE TEMP TABLE _purge_events AS
SELECT id FROM {{schema}}.events
 WHERE namespace_id = @p_namespace_id
   AND created_at_utc <= {{now}} - (@p_events_retention_days) * 86400000
 ORDER BY created_at_utc, id
 LIMIT (@p_batch_size * @p_max_iterations);

DELETE FROM {{schema}}.tags WHERE scope_code = 90 /* TagScopeCode.Event */ AND scope_id IN (SELECT id FROM temp._purge_events);
DELETE FROM {{schema}}.events WHERE id IN (SELECT id FROM temp._purge_events);

CREATE TEMP TABLE _purge_alerts AS
SELECT id FROM {{schema}}.alerts
 WHERE namespace_id = @p_namespace_id
   AND created_at_utc <= {{now}} - (@p_alert_retention_days) * 86400000
   AND delivery_status_code IN (30 /* AlertDeliveryStatusCode.Suppressed */, 100 /* AlertDeliveryStatusCode.Delivered */, 200 /* AlertDeliveryStatusCode.Failed */)
 ORDER BY created_at_utc, id
 LIMIT (@p_batch_size * @p_max_iterations);

DELETE FROM {{schema}}.tags WHERE scope_code = 80 /* TagScopeCode.Alert */ AND scope_id IN (SELECT id FROM temp._purge_alerts);
DELETE FROM {{schema}}.alerts WHERE id IN (SELECT id FROM temp._purge_alerts);

CREATE TEMP TABLE _purge_workers AS
SELECT id FROM {{schema}}.workers
 WHERE namespace_id = @p_namespace_id
   AND status_code = 200 /* WorkerStatusCode.Dead */
   AND last_seen_at_utc <= {{now}} - (@p_worker_retention_seconds) * 1000
 ORDER BY last_seen_at_utc, id
 LIMIT (@p_batch_size * @p_max_iterations);

DELETE FROM {{schema}}.tags WHERE scope_code = 70 /* TagScopeCode.Worker */ AND scope_id IN (SELECT id FROM temp._purge_workers);
DELETE FROM {{schema}}.workers WHERE id IN (SELECT id FROM temp._purge_workers);

CREATE TEMP TABLE _purge_locks AS
SELECT lease_key FROM {{schema}}.leases
 WHERE kind_code = 10 /* LeaseKindCode.Lock */
   AND expires_at_utc <= {{now}}
 ORDER BY expires_at_utc
 LIMIT (@p_batch_size * @p_max_iterations);

DELETE FROM {{schema}}.leases WHERE lease_key IN (SELECT lease_key FROM temp._purge_locks);

SELECT
    (SELECT COUNT(*) FROM temp._purge_jobs)    AS jobs_deleted,
    (SELECT COUNT(*) FROM temp._purge_events)  AS events_deleted,
    (SELECT COUNT(*) FROM temp._purge_alerts)  AS alerts_deleted,
    (SELECT COUNT(*) FROM temp._purge_workers) AS workers_deleted,
    (SELECT COUNT(*) FROM temp._purge_locks)   AS locks_deleted;
