-- One batch, one implicit transaction; only the final SELECT returns rows. The job's rows are locked first,
-- and every write waits on one decision row.
DROP TABLE IF EXISTS pg_temp._purge_job;
DROP TABLE IF EXISTS pg_temp._purge_job_action;
CREATE TEMP TABLE _purge_job (
    from_status SMALLINT NOT NULL,
    namespace_id INT NOT NULL,
    definition_id INT NOT NULL,
    tenant_id INT,
    lineage_root_job_id BIGINT,
    parent_job_id BIGINT
) ON COMMIT DROP;
CREATE TEMP TABLE _purge_job_action (action SMALLINT NOT NULL, blocked BOOLEAN NOT NULL) ON COMMIT DROP;

INSERT INTO _purge_job (from_status, namespace_id, definition_id, tenant_id, lineage_root_job_id, parent_job_id)
SELECT r.job_status_code, j.namespace_id, j.definition_id, j.tenant_id, j.lineage_root_job_id, j.parent_job_id
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
WHERE j.job_id = @p_job_id
FOR UPDATE OF j, r;

/* parent_job_id carries no FK, so a job with children is kept (their lineage would dangle), and so is a finished child of a
   live parent: the parent's replay dedupes onto it and reads its result. A purgeable job the alert projector still owes is
   refused below: an event it reads, of an alert-enabled definition, past the floor. */
INSERT INTO _purge_job_action (action, blocked)
SELECT
    CASE
        WHEN s.from_status IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN s.from_status NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
            THEN 3 /* ControlAction.Rejected */
        WHEN EXISTS (SELECT 1 FROM {{schema}}.jobs c WHERE c.parent_job_id = @p_job_id) THEN 3 /* ControlAction.Rejected */
        WHEN EXISTS (
            SELECT 1
            FROM {{schema}}.jobs_state p
            WHERE
                p.job_id = s.parent_job_id
                AND p.job_status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
        ) THEN 3 /* ControlAction.Rejected */
        ELSE 1 /* ControlAction.Applied */
    END,
    EXISTS (
        SELECT 1
        FROM {{schema}}.events e
        JOIN {{schema}}.definitions d ON d.definition_id = e.definition_id
        WHERE
            e.entity_code = 50 /* EntityCode.Job */
            AND e.entity_id = @p_job_id
            AND e.created_at_utc >= @p_alert_floor_utc
            AND (e.created_at_utc > @p_alert_floor_utc OR e.event_id > @p_alert_floor_event_id)
            AND (
                e.event_code IN (41 /* EventCode.JobExecutionFailed */, 42 /* EventCode.JobExecutionOrphaned */, 44 /* EventCode.JobFailed */)
                OR (e.event_code = 40 /* EventCode.JobExecutionSucceeded */ AND e.execution_number > 1)
            )
            AND d.alert_profile_code_effective <> 0 /* AlertProfileCode.None */
    )
FROM (SELECT 1) one
LEFT JOIN _purge_job s ON true;

-- The anonymous block only raises.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_temp._purge_job_action WHERE action = 1 /* ControlAction.Applied */ AND blocked) THEN
        RAISE EXCEPTION 'ACTA:PURGE_BLOCKED:purge_job: the alert projector has not processed this job''s events' USING ERRCODE = 'P0001';
    END IF;
END
$$;

DELETE FROM {{schema}}.tags
WHERE
    entity_code = 50 /* EntityCode.Job */
    AND entity_id = @p_job_id
    AND EXISTS (SELECT 1 FROM _purge_job_action a WHERE a.action = 1 /* ControlAction.Applied */);

DELETE FROM {{schema}}.events
WHERE
    entity_code = 50 /* EntityCode.Job */
    AND entity_id = @p_job_id
    AND EXISTS (SELECT 1 FROM _purge_job_action a WHERE a.action = 1 /* ControlAction.Applied */);
DELETE FROM {{schema}}.alerts
WHERE job_id = @p_job_id AND EXISTS (SELECT 1 FROM _purge_job_action a WHERE a.action = 1 /* ControlAction.Applied */);
DELETE FROM {{schema}}.checkpoints
WHERE job_id = @p_job_id AND EXISTS (SELECT 1 FROM _purge_job_action a WHERE a.action = 1 /* ControlAction.Applied */);
DELETE FROM {{schema}}.steps
WHERE job_id = @p_job_id AND EXISTS (SELECT 1 FROM _purge_job_action a WHERE a.action = 1 /* ControlAction.Applied */);
DELETE FROM {{schema}}.schedules
WHERE job_id = @p_job_id AND EXISTS (SELECT 1 FROM _purge_job_action a WHERE a.action = 1 /* ControlAction.Applied */);
DELETE FROM {{schema}}.schedules_state
WHERE job_id = @p_job_id AND EXISTS (SELECT 1 FROM _purge_job_action a WHERE a.action = 1 /* ControlAction.Applied */);
DELETE FROM {{schema}}.jobs
WHERE job_id = @p_job_id AND EXISTS (SELECT 1 FROM _purge_job_action a WHERE a.action = 1 /* ControlAction.Applied */);
DELETE FROM {{schema}}.jobs_state
WHERE job_id = @p_job_id AND EXISTS (SELECT 1 FROM _purge_job_action a WHERE a.action = 1 /* ControlAction.Applied */);

-- The tombstone keeps the deleted job's id as its subject; its details snapshot what no surviving event carries.
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, entity_code, entity_id, definition_id, tenant_id, lineage_root_job_id,
    event_message, details_format_id, details)
SELECT
    75 /* EventCode.JobPurged */, now(), s.namespace_id, 50 /* EntityCode.Job */, @p_job_id, s.definition_id, s.tenant_id,
    COALESCE(s.lineage_root_job_id, @p_job_id), @p_event_message, @p_details_format_id, @p_details
FROM _purge_job s
WHERE EXISTS (SELECT 1 FROM _purge_job_action a WHERE a.action = 1 /* ControlAction.Applied */);

SELECT
    a.action,
    CASE WHEN a.action = 3 /* ControlAction.Rejected */ THEN s.from_status END AS status
FROM _purge_job_action a
LEFT JOIN _purge_job s ON true;
