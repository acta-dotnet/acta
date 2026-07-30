DROP TABLE IF EXISTS temp._resume_job;

CREATE TEMP TABLE _resume_job AS
SELECT j.id, r.status_code AS from_status
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
WHERE j.id = @p_id;

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    actor_code, actor_key,
    job_id, job_ref, execution_number,
    lineage_root_id, definition_id, tenant_id,
    worker_id,
    from_status_code, to_status_code,
    execution_status_code, duration_ms,
    reason_code, reason_message)
SELECT
    72 /* JobEventCode.JobResumed */, {{now}}, j.namespace_id,
    @p_actor_code, @p_actor_key,
    j.id, j.job_ref, r.execution_number,
    COALESCE(j.lineage_root_id, j.id), j.definition_id, j.tenant_id,
    NULL,
    30 /* JobStatusCode.Paused */, 10 /* JobStatusCode.Ready */,
    NULL, NULL,
    @p_reason_code, @p_reason_message
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
WHERE j.id = @p_id
  AND j.audit_level_code = 20 /* JobAuditLevelCode.Audit */
  AND r.status_code = 30 /* JobStatusCode.Paused */;

UPDATE {{schema}}.runtimes
   SET status_code      = 10 /* JobStatusCode.Ready */,
       next_run_at_utc  = COALESCE(@p_next_run_at_utc, {{now}}),
       modified_at_utc  = {{now}},
       version          = version + 1
 WHERE job_id = @p_id
   AND status_code = 30 /* JobStatusCode.Paused */;

SELECT
    CASE
        WHEN s.id IS NULL THEN 2 /* JobControlAction.NotFound */
        WHEN s.from_status = 30 /* JobStatusCode.Paused */ THEN 1 /* JobControlAction.Applied */
        ELSE 3 /* JobControlAction.Rejected */
    END AS action,
    CASE
        WHEN s.id IS NULL THEN NULL
        WHEN s.from_status = 30 /* JobStatusCode.Paused */ THEN 10 /* JobStatusCode.Ready */
        ELSE s.from_status
    END AS status_code
FROM (SELECT @p_id AS qid) q
LEFT JOIN temp._resume_job s ON s.id = q.qid;
