-- Runs inside the caller's redrive transaction under the single writer: the events and the tag copy are
-- written only when the version CAS below will land, and the bump comes last so its row is the result.
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
    reason_message,
    detail_format_id,
    detail)
SELECT
    77 /* EventCode.JobRedriven */,
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
    CASE WHEN j.id = @p_id THEN r.status_code END,
    r.status_code,
    NULL,
    NULL,
    @p_reason_code,
    @p_reason_message,
    1 /* JobPayloadFormat.Json */,
    CASE WHEN j.id = @p_id THEN @p_detail ELSE @p_redrive_detail END
FROM {{schema}}.jobs j
INNER JOIN {{schema}}.runtimes r ON r.job_id = j.id
WHERE
    j.id IN (@p_id, @p_redrive_job_id)
    AND j.audit_level_code = 20 /* JobAuditLevelCode.Audit */
    AND EXISTS (
        SELECT 1
        FROM {{schema}}.runtimes f
        WHERE
            f.job_id = @p_id
            AND f.status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
            AND (@p_expected_version IS NULL OR f.version = @p_expected_version));

INSERT INTO {{schema}}.tags (scope_code, scope_id, namespace_id, name, value, value_search)
SELECT t.scope_code, @p_redrive_job_id, t.namespace_id, t.name, t.value, t.value_search
FROM {{schema}}.tags t
WHERE
    t.scope_code = 50 /* TagScopeCode.Job */
    AND t.scope_id = @p_id
    AND EXISTS (
        SELECT 1
        FROM {{schema}}.runtimes f
        WHERE
            f.job_id = @p_id
            AND f.status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
            AND (@p_expected_version IS NULL OR f.version = @p_expected_version));

UPDATE {{schema}}.runtimes
SET version = version + 1
WHERE
    job_id = @p_id
    AND status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
    AND (@p_expected_version IS NULL OR version = @p_expected_version)
RETURNING version, status_code;
