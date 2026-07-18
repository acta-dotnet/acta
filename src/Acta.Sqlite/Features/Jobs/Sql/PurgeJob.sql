DROP TABLE IF EXISTS temp._purge_job;

CREATE TEMP TABLE _purge_job AS
SELECT j.id, r.status_code AS from_status, j.namespace_id, j.definition_id, j.tenant_id, j.job_ref, d.name AS job_name,
       CASE WHEN EXISTS (SELECT 1 FROM {{schema}}.jobs c WHERE c.parent_id = j.id) THEN 1 ELSE 0 END AS has_child
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
JOIN {{schema}}.definitions d ON d.id = j.definition_id
WHERE j.id = @p_id;

DROP TABLE IF EXISTS temp._purge_schedules;
DROP TABLE IF EXISTS temp._purge_alerts;
DROP TABLE IF EXISTS temp._purge_events;

CREATE TEMP TABLE _purge_schedules AS SELECT id FROM {{schema}}.schedules WHERE job_id = @p_id;
CREATE TEMP TABLE _purge_alerts AS SELECT id FROM {{schema}}.alerts WHERE job_id = @p_id;
CREATE TEMP TABLE _purge_events AS SELECT id FROM {{schema}}.events WHERE job_id = @p_id;

DELETE FROM {{schema}}.tags
 WHERE EXISTS (SELECT 1 FROM temp._purge_job s WHERE s.from_status IN (100 /* JobStatusCode.Done */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */) AND s.has_child = 0)
   AND ((scope_code = 50 /* TagScopeCode.Job */ AND scope_id = @p_id)
     OR (scope_code = 60 /* TagScopeCode.Schedule */ AND scope_id IN (SELECT id FROM temp._purge_schedules))
     OR (scope_code = 80 /* TagScopeCode.Alert */ AND scope_id IN (SELECT id FROM temp._purge_alerts))
     OR (scope_code = 90 /* TagScopeCode.Event */ AND scope_id IN (SELECT id FROM temp._purge_events)));

DELETE FROM {{schema}}.events WHERE job_id = @p_id
  AND EXISTS (SELECT 1 FROM temp._purge_job s WHERE s.id = @p_id AND s.from_status IN (100 /* JobStatusCode.Done */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */) AND s.has_child = 0);

DELETE FROM {{schema}}.alerts WHERE job_id = @p_id
  AND EXISTS (SELECT 1 FROM temp._purge_job s WHERE s.id = @p_id AND s.from_status IN (100 /* JobStatusCode.Done */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */) AND s.has_child = 0);

DELETE FROM {{schema}}.jobs WHERE id = @p_id
  AND EXISTS (SELECT 1 FROM temp._purge_job s WHERE s.id = @p_id AND s.from_status IN (100 /* JobStatusCode.Done */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */) AND s.has_child = 0);

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    actor_code, actor_key,
    job_id, job_ref, execution_number,
    lineage_root_id, definition_id, tenant_id,
    worker_id, from_status_code, to_status_code,
    execution_status_code, duration_ms,
    reason_code, reason_message)
SELECT
    75 /* JobEventCode.JobPurged */, {{now}}, s.namespace_id,
    @p_actor_code, @p_actor_key,
    NULL, NULL, NULL,
    NULL, s.definition_id, s.tenant_id,
    NULL, s.from_status, NULL,
    NULL, NULL,
    @p_reason_code, 'purged ' || s.job_ref || ' (' || s.job_name || ')'
FROM temp._purge_job s
WHERE s.id = @p_id
  AND s.from_status IN (100 /* JobStatusCode.Done */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
  AND s.has_child = 0;

SELECT
    CASE
        WHEN s.id IS NULL THEN 2 /* JobControlAction.NotFound */
        WHEN s.from_status NOT IN (100 /* JobStatusCode.Done */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */) THEN 3 /* JobControlAction.Rejected */
        WHEN s.has_child = 1 THEN 3 /* JobControlAction.Rejected */
        ELSE 1 /* JobControlAction.Applied */
    END AS action,
    CASE
        WHEN s.id IS NULL THEN NULL
        WHEN s.from_status NOT IN (100 /* JobStatusCode.Done */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */) THEN s.from_status
        WHEN s.has_child = 1 THEN s.from_status
        ELSE NULL
    END AS status_code
FROM (SELECT @p_id AS qid) q
LEFT JOIN temp._purge_job s ON s.id = q.qid;
