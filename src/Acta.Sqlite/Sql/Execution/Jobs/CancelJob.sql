DROP TABLE IF EXISTS temp._cancel_job;

CREATE TEMP TABLE _cancel_job AS
SELECT j.id, r.status_code AS from_status, j.parent_id
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
WHERE j.id = @p_id;

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
    tenant_id,
    worker_id,
    from_status_code,
    to_status_code,
    execution_status_code,
    duration_ms,
    reason_code,
    reason_message)
SELECT
    41 /* JobEventCode.JobExecutionFinished */,
    {{now}},
    j.namespace_id,
    @p_actor_code,
    @p_actor_key,
    j.id,
    j.job_ref,
    r.execution_number,
    COALESCE(j.lineage_root_id, j.id),
    j.definition_id,
    j.tenant_id,
    r.leased_by_worker_id,
    50 /* JobStatusCode.Executing */,
    220 /* JobStatusCode.Cancelled */,
    220 /* ExecutionStatusCode.Cancelled */,
    NULL,
    @p_reason_code,
    @p_reason_message
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
WHERE
    j.id = @p_id
    AND j.audit_level_code = 20 /* JobAuditLevelCode.Audit */
    AND r.status_code = 50 /* JobStatusCode.Executing */;

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
    tenant_id,
    worker_id,
    from_status_code,
    to_status_code,
    execution_status_code,
    duration_ms,
    reason_code,
    reason_message)
SELECT
    70 /* JobEventCode.JobCancelled */,
    {{now}},
    j.namespace_id,
    @p_actor_code,
    @p_actor_key,
    j.id,
    j.job_ref,
    r.execution_number,
    COALESCE(j.lineage_root_id, j.id),
    j.definition_id,
    j.tenant_id,
    NULL,
    r.status_code,
    220 /* JobStatusCode.Cancelled */,
    NULL,
    NULL,
    @p_reason_code,
    @p_reason_message
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
WHERE
    j.id = @p_id
    AND j.audit_level_code = 20 /* JobAuditLevelCode.Audit */
    AND r.status_code IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, 40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */);

UPDATE {{schema}}.runtimes
SET
    status_code = 220 /* JobStatusCode.Cancelled */,
    leased_by_worker_id = NULL,
    lease_expires_at_utc = NULL,
    retention_until_utc = {{now}} + (
        SELECT jd.retention_seconds_effective
        FROM {{schema}}.definitions jd
        JOIN {{schema}}.jobs j2 ON j2.id = runtimes.job_id
        WHERE jd.id = j2.definition_id
    ) * 1000,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_id
    AND status_code IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, 40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */);

SELECT
    CASE
        WHEN s.id IS NULL THEN 2 /* JobControlAction.NotFound */
        WHEN s.from_status IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, 40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */) THEN 1 /* JobControlAction.Applied */
        ELSE 3 /* JobControlAction.Rejected */
    END AS action,
    CASE
        WHEN s.id IS NULL THEN NULL
        WHEN s.from_status IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, 40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */) THEN 220 /* JobStatusCode.Cancelled */
        ELSE s.from_status
    END AS status_code,
    s.parent_id AS parent_id
FROM (SELECT @p_id AS qid) q
LEFT JOIN temp._cancel_job s ON s.id = q.qid;
