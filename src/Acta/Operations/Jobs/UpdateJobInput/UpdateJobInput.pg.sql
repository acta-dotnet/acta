-- One batch, one implicit transaction; only the final SELECT returns rows. The job's rows are locked, and a
-- job that is not in flight takes the new input.
DROP TABLE IF EXISTS pg_temp._update_job_input;
CREATE TEMP TABLE _update_job_input (
    from_status SMALLINT NOT NULL,
    namespace_id INT NOT NULL,
    lineage_root_id BIGINT,
    definition_id INT NOT NULL,
    tenant_id INT,
    execution_number INT NOT NULL,
    audit_level_code SMALLINT NOT NULL,
    job_ref UUID NOT NULL,
    old_format_id SMALLINT NOT NULL,
    old_bytes INT
) ON COMMIT DROP;

INSERT INTO _update_job_input (
    from_status, namespace_id, lineage_root_id, definition_id, tenant_id, execution_number, audit_level_code, job_ref,
    old_format_id, old_bytes)
SELECT
    r.status_code, j.namespace_id, j.lineage_root_id, j.definition_id, j.tenant_id, r.execution_number, j.audit_level_code,
    j.job_ref, j.input_format_id, octet_length(j.input)
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
WHERE j.id = @p_id
FOR UPDATE;

UPDATE {{schema}}.jobs
SET
    input = @p_input,
    input_format_id = @p_input_format_id
WHERE
    id = @p_id
    AND EXISTS (
        SELECT 1 FROM _update_job_input s
        WHERE s.from_status <> 50 /* JobStatusCode.Executing */
    );

-- The event carries only bounded JSON metadata about the previous payload (format name and byte count), never
-- the payload itself, so nothing outlives the job's payload retention.
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, actor_code, actor, job_id, job_ref, execution_number,
    lineage_root_id, definition_id, tenant_id, detail_format_id, detail, reason_code, reason_message)
SELECT
    76 /* EventCode.JobInputAmended */, now(), s.namespace_id, @p_actor_code, @p_actor, @p_id, s.job_ref,
    s.execution_number, COALESCE(s.lineage_root_id, @p_id), s.definition_id, s.tenant_id,
    CASE WHEN s.old_format_id <> 0 /* JobPayloadFormat.None */ THEN 1 /* JobPayloadFormat.Json */ ELSE 0 /* JobPayloadFormat.None */ END,
    CASE WHEN s.old_format_id <> 0 /* JobPayloadFormat.None */ THEN convert_to(
        '{"format":"'
        || CASE s.old_format_id
            WHEN 1 /* JobPayloadFormat.Json */ THEN 'json'
            WHEN 2 /* JobPayloadFormat.Bytes */ THEN 'bytes'
            WHEN 3 /* JobPayloadFormat.Text */ THEN 'text'
            ELSE 'custom-' || s.old_format_id::text
        END
        || '","bytes":' || s.old_bytes::text || '}',
        'UTF8') END,
    @p_reason_code, @p_reason_message
FROM _update_job_input s
WHERE
    s.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */
    AND s.from_status <> 50 /* JobStatusCode.Executing */;

SELECT
    CASE
        WHEN s.from_status IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN s.from_status = 50 /* JobStatusCode.Executing */ THEN 3 /* ControlAction.Rejected */
        ELSE 1 /* ControlAction.Applied */
    END::SMALLINT AS action,
    s.from_status AS status_code
FROM (SELECT 1) one
LEFT JOIN _update_job_input s ON true;
