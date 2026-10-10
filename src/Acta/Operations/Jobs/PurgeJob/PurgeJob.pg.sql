-- One batch, one implicit transaction; only the final SELECT returns rows. Canonical deletion lock order:
-- the job's rows, then its schedules, alerts, and events, then tags. Every write waits on one decision row.
DROP TABLE IF EXISTS pg_temp._purge_job;
DROP TABLE IF EXISTS pg_temp._purge_job_action;
DROP TABLE IF EXISTS pg_temp._purge_job_locked;
CREATE TEMP TABLE _purge_job (
    from_status SMALLINT NOT NULL,
    namespace_id INT NOT NULL,
    definition_id INT NOT NULL,
    tenant_id INT,
    job_ref UUID NOT NULL,
    job_name VARCHAR NOT NULL,
    parent_id BIGINT
) ON COMMIT DROP;
CREATE TEMP TABLE _purge_job_action (action SMALLINT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _purge_job_locked (id BIGINT NOT NULL) ON COMMIT DROP;

INSERT INTO _purge_job (from_status, namespace_id, definition_id, tenant_id, job_ref, job_name, parent_id)
SELECT r.status_code, j.namespace_id, j.definition_id, j.tenant_id, j.job_ref, d.name, j.parent_id
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
JOIN {{schema}}.definitions d ON d.id = j.definition_id
WHERE j.id = @p_id
FOR UPDATE OF j, r;

-- parent_id carries no FK, so a job with children is kept (their lineage would dangle), and so is a finished
-- child of a live parent: the parent's replay dedupes onto it and reads its result.
INSERT INTO _purge_job_action (action)
SELECT
    CASE
        WHEN s.from_status IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN s.from_status NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
            THEN 3 /* ControlAction.Rejected */
        WHEN EXISTS (SELECT 1 FROM {{schema}}.jobs c WHERE c.parent_id = @p_id) THEN 3 /* ControlAction.Rejected */
        WHEN EXISTS (
            SELECT 1
            FROM {{schema}}.runtimes p
            WHERE
                p.job_id = s.parent_id
                AND p.status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
        ) THEN 3 /* ControlAction.Rejected */
        ELSE 1 /* ControlAction.Applied */
    END
FROM (SELECT 1) one
LEFT JOIN _purge_job s ON true;

INSERT INTO _purge_job_locked (id)
SELECT s.id FROM {{schema}}.schedules s
WHERE s.job_id = @p_id AND EXISTS (SELECT 1 FROM _purge_job_action a WHERE a.action = 1 /* ControlAction.Applied */)
ORDER BY s.id
FOR UPDATE;

INSERT INTO _purge_job_locked (id)
SELECT a.id FROM {{schema}}.alerts a
WHERE a.job_id = @p_id AND EXISTS (SELECT 1 FROM _purge_job_action x WHERE x.action = 1 /* ControlAction.Applied */)
ORDER BY a.id
FOR UPDATE;

INSERT INTO _purge_job_locked (id)
SELECT e.id FROM {{schema}}.events e
WHERE e.job_id = @p_id AND EXISTS (SELECT 1 FROM _purge_job_action a WHERE a.action = 1 /* ControlAction.Applied */)
ORDER BY e.id
FOR UPDATE;

DELETE FROM {{schema}}.tags t
WHERE
    EXISTS (SELECT 1 FROM _purge_job_action a WHERE a.action = 1 /* ControlAction.Applied */)
    AND (
        (t.scope_code = 50 /* TagScopeCode.Job */ AND t.scope_id = @p_id)
        OR (t.scope_code = 60 /* TagScopeCode.Schedule */ AND t.scope_id IN (SELECT s.id FROM {{schema}}.schedules s WHERE s.job_id = @p_id))
        OR (t.scope_code = 80 /* TagScopeCode.Alert */ AND t.scope_id IN (SELECT a.id FROM {{schema}}.alerts a WHERE a.job_id = @p_id))
        OR (t.scope_code = 90 /* TagScopeCode.Event */ AND t.scope_id IN (SELECT e.id FROM {{schema}}.events e WHERE e.job_id = @p_id))
    );

DELETE FROM {{schema}}.events
WHERE job_id = @p_id AND EXISTS (SELECT 1 FROM _purge_job_action a WHERE a.action = 1 /* ControlAction.Applied */);
DELETE FROM {{schema}}.alerts
WHERE job_id = @p_id AND EXISTS (SELECT 1 FROM _purge_job_action a WHERE a.action = 1 /* ControlAction.Applied */);
DELETE FROM {{schema}}.jobs
WHERE id = @p_id AND EXISTS (SELECT 1 FROM _purge_job_action a WHERE a.action = 1 /* ControlAction.Applied */);

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, actor_code, actor, definition_id, tenant_id, from_status_code,
    reason_code, reason_message)
SELECT
    75 /* EventCode.JobPurged */, now(), s.namespace_id, @p_actor_code, @p_actor, s.definition_id, s.tenant_id,
    s.from_status, @p_reason_code, 'purged ' || s.job_ref::text || ' (' || s.job_name || ')'
FROM _purge_job s
WHERE EXISTS (SELECT 1 FROM _purge_job_action a WHERE a.action = 1 /* ControlAction.Applied */);

SELECT
    a.action AS action,
    CASE WHEN a.action = 3 /* ControlAction.Rejected */ THEN s.from_status END AS status_code
FROM _purge_job_action a
LEFT JOIN _purge_job s ON true;
