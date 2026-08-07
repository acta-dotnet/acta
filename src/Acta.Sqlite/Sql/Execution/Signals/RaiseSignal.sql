INSERT INTO {{schema}}.checkpoints (job_id, kind_code, name, status_code, value_format_id, value, modified_at_utc, version)
SELECT
    @p_job_id,
    @p_kind_code,
    @p_name,
    20 /* JobCheckpointStatusCode.Set */,
    @p_value_format_id,
    @p_value,
    {{now}},
    0
WHERE
    EXISTS (
        SELECT 1 FROM {{schema}}.jobs j
        JOIN {{schema}}.runtimes r ON r.job_id = j.id
        WHERE
            j.id = @p_job_id
            AND r.status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
    )
ON CONFLICT (job_id, kind_code, name) DO UPDATE
SET
    status_code = 20 /* JobCheckpointStatusCode.Set */,
    value_format_id = @p_value_format_id,
    value = @p_value,
    modified_at_utc = {{now}},
    version = {{schema}}.checkpoints.version + 1;

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, actor_code, actor_key, job_id, job_ref, execution_number,
    lineage_root_id, definition_id, tenant_id, worker_id, from_status_code, to_status_code,
    execution_status_code, duration_ms, reason_code, reason_message
)
SELECT
    80 /* JobEventCode.JobSignalRaised */,
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
    r.status_code,
    NULL,
    NULL,
    @p_reason_code,
    @p_reason_message
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
WHERE
    j.id = @p_job_id
    AND r.status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
    AND j.audit_level_code = 20 /* JobAuditLevelCode.Audit */;

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, actor_code, actor_key, job_id, job_ref, execution_number,
    lineage_root_id, definition_id, tenant_id, worker_id, from_status_code, to_status_code,
    execution_status_code, duration_ms, reason_code, reason_message
)
SELECT
    72 /* JobEventCode.JobResumed */,
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
    20 /* JobStatusCode.Suspended */,
    10 /* JobStatusCode.Ready */,
    NULL,
    NULL,
    @p_reason_code,
    @p_reason_message
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
WHERE
    j.id = @p_job_id
    AND r.status_code = 20 /* JobStatusCode.Suspended */
    AND j.audit_level_code = 20 /* JobAuditLevelCode.Audit */;

UPDATE {{schema}}.runtimes
SET
    status_code = 10 /* JobStatusCode.Ready */,
    next_run_at_utc = {{now}},
    modified_at_utc = {{now}},
    version = version + 1
WHERE job_id = @p_job_id AND status_code = 20 /* JobStatusCode.Suspended */;

SELECT
    CASE
        WHEN r.job_id IS NULL THEN 2 /* JobControlAction.NotFound */
        WHEN
            r.status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
            THEN 3 /* JobControlAction.Rejected */
        ELSE 1 /* JobControlAction.Applied */
    END AS action,
    r.status_code AS status_code
FROM (SELECT @p_job_id AS id) probe
LEFT JOIN {{schema}}.runtimes r ON r.job_id = @p_job_id;
