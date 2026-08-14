DROP TABLE IF EXISTS temp._rs_target;

CREATE TEMP TABLE _rs_target AS
SELECT js.id AS schedule_id
FROM {{schema}}.schedules js
WHERE
    js.job_id = @p_job_id
    AND js.name = @p_name
    AND js.status_code <> 230 /* ScheduleStatusCode.Orphaned */;

INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    actor_code,
    actor_key,
    job_id,
    job_ref,
    execution_number,
    lineage_root_id,
    definition_id,
    worker_id,
    from_status_code,
    to_status_code,
    execution_status_code,
    duration_ms,
    reason_code,
    reason_message)
SELECT
    101 /* EventCode.ScheduleResumed */,
    {{now}},
    j.namespace_id,
    @p_actor_code,
    @p_actor_key,
    j.id,
    j.job_ref,
    r.execution_number,
    COALESCE(j.lineage_root_id, j.id),
    j.definition_id,
    NULL,
    NULL,
    NULL,
    NULL,
    NULL,
    NULL,
    @p_name
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
WHERE
    j.id = @p_job_id
    AND j.audit_level_code = 20 /* JobAuditLevelCode.Audit */
    AND EXISTS (SELECT 1 FROM temp._rs_target);

UPDATE {{schema}}.schedules
SET
    status_code = 10 /* ScheduleStatusCode.Active */,
    paused_until_utc = NULL,
    next_run_at_utc = @p_next_run_at_utc,
    reason_message = @p_reason_message,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_job_id
    AND name = @p_name
    AND status_code <> 230 /* ScheduleStatusCode.Orphaned */;

UPDATE {{schema}}.runtimes
SET
    next_run_at_utc = @p_job_next_run_at_utc,
    status_code = CASE WHEN @p_job_next_run_at_utc IS NULL
        THEN 30 /* JobStatusCode.Paused */
        ELSE 10 /* JobStatusCode.Ready */ END,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_job_id
    AND status_code IN (30 /* JobStatusCode.Paused */, 10 /* JobStatusCode.Ready */);

SELECT
    CASE
        WHEN t.schedule_id IS NULL THEN 2 /* ControlAction.NotFound */
        ELSE 1 /* ControlAction.Applied */
    END AS action,
    js.status_code AS status_code,
    js.paused_until_utc AS paused_until_utc,
    js.next_run_at_utc AS next_run_at_utc,
    js.version AS version
FROM (SELECT @p_job_id AS qid) q
LEFT JOIN temp._rs_target t ON 1 = 1
LEFT JOIN {{schema}}.schedules js ON js.id = t.schedule_id;
