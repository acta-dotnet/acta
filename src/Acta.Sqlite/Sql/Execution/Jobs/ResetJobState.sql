INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    actor_code, actor_key,
    job_id, job_ref, execution_number,
    lineage_root_id, definition_id, tenant_id,
    worker_id,
    from_status_code, to_status_code,
    execution_status_code, duration_ms,
    reason_code, reason_message
)
SELECT
    81 /* EventCode.JobStateReset */,
    {{now}},
    j.namespace_id,
    50 /* ActorCode.Job */,
    CAST(j.id AS TEXT),
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
    NULL,
    NULL
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
WHERE
    j.id = @p_id
    AND j.audit_level_code = 20 /* JobAuditLevelCode.Audit */;

DELETE FROM {{schema}}.checkpoints
WHERE job_id = @p_id;
DELETE FROM {{schema}}.steps
WHERE job_id = @p_id;
DELETE FROM {{schema}}.results
WHERE job_id = @p_id;
