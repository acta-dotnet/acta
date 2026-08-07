DROP TABLE IF EXISTS temp._tsn_target;

CREATE TEMP TABLE _tsn_target AS
SELECT
    js.id AS schedule_id,
    CASE
        WHEN js.status_code = 30 /* ScheduleStatusCode.Paused */ THEN 3 /* JobControlAction.Rejected */
        WHEN r.status_code IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */) THEN 3 /* JobControlAction.Rejected */
        WHEN r.status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */) THEN 3 /* JobControlAction.Rejected */
        ELSE 1 /* JobControlAction.Applied */
    END AS action
FROM {{schema}}.schedules js
JOIN {{schema}}.runtimes r ON r.job_id = js.job_id
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
    104 /* JobEventCode.ScheduleTriggered */,
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
    @p_reason_message
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
WHERE
    j.id = @p_job_id
    AND j.audit_level_code = 20 /* JobAuditLevelCode.Audit */
    AND EXISTS (SELECT 1 FROM temp._tsn_target WHERE action = 1 /* JobControlAction.Applied */);

UPDATE {{schema}}.runtimes
SET
    next_run_at_utc = {{now}},
    status_code = 10 /* JobStatusCode.Ready */,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_job_id
    AND status_code IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */)
    AND EXISTS (SELECT 1 FROM temp._tsn_target WHERE action = 1 /* JobControlAction.Applied */);

SELECT
    CASE WHEN t.schedule_id IS NULL THEN 2 /* JobControlAction.NotFound */ ELSE t.action END AS action,
    js.status_code AS status_code,
    js.paused_until_utc AS paused_until_utc,
    js.next_run_at_utc AS next_run_at_utc,
    js.version AS version
FROM (SELECT @p_job_id AS qid) q
LEFT JOIN temp._tsn_target t ON 1 = 1
LEFT JOIN {{schema}}.schedules js ON js.id = t.schedule_id;
