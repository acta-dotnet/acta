CREATE OR REPLACE FUNCTION {{schema}}.update_job_input(
    p_id              BIGINT,
    p_input_format_id SMALLINT,
    p_input           BYTEA,
    p_actor_code      SMALLINT,
    p_actor_key       VARCHAR,
    p_reason_code     SMALLINT,
    p_reason_message  VARCHAR
)
RETURNS TABLE(action SMALLINT, status_code SMALLINT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_from_status   SMALLINT;
    v_namespace_id  SMALLINT;
    v_lineage       BIGINT;
    v_definition    INT;
    v_tenant        INT;
    v_en            INT;
    v_audit         SMALLINT;
    v_job_ref       UUID;
    v_old_format_id SMALLINT;
    v_old_input     BYTEA;
    v_detail_format_id SMALLINT := 0 /* JobPayloadFormat.None */;
    v_detail        BYTEA;
BEGIN
    SELECT r.status_code, j.namespace_id, j.lineage_root_id, j.definition_id, j.tenant_id, r.execution_number, j.audit_level_code,
           j.job_ref, j.input_format_id, j.input
      INTO v_from_status, v_namespace_id, v_lineage, v_definition, v_tenant, v_en, v_audit, v_job_ref, v_old_format_id, v_old_input
      FROM {{schema}}.jobs j
      JOIN {{schema}}.runtimes r ON r.job_id = j.id
     WHERE j.id = p_id
     FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 2 /* JobControlAction.NotFound */::SMALLINT, NULL::SMALLINT;
        RETURN;
    END IF;

    IF v_from_status IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */) THEN
        RETURN QUERY SELECT 3 /* JobControlAction.Rejected */::SMALLINT, v_from_status;
        RETURN;
    END IF;

    UPDATE {{schema}}.jobs
       SET input           = p_input,
           input_format_id = p_input_format_id
     WHERE id = p_id;

    IF v_audit = 20 /* JobAuditLevelCode.Audit */ THEN
        -- The event carries only bounded JSON metadata about the previous payload (format name and
        -- byte count), never the payload itself, so nothing outlives the job's payload retention.
        IF v_old_format_id <> 0 /* JobPayloadFormat.None */ THEN
            v_detail_format_id := 1 /* JobPayloadFormat.Json */;
            v_detail := convert_to(
                '{"format":"'
                    || CASE v_old_format_id
                           WHEN 1 /* JobPayloadFormat.Json */ THEN 'json'
                           WHEN 2 /* JobPayloadFormat.Bytes */ THEN 'bytes'
                           WHEN 3 /* JobPayloadFormat.Text */ THEN 'text'
                           ELSE 'custom-' || v_old_format_id::text
                       END
                    || '","bytes":' || octet_length(v_old_input)::text || '}',
                'UTF8');
        END IF;

        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id,
            actor_code, actor_key,
            job_id, job_ref, execution_number,
            lineage_root_id, definition_id, tenant_id,
            worker_id,
            from_status_code, to_status_code,
            execution_status_code, duration_ms,
            detail_format_id, detail,
            reason_code, reason_message)
        VALUES (
            76 /* JobEventCode.JobInputAmended */, now(), v_namespace_id,
            p_actor_code, p_actor_key,
            p_id, v_job_ref, v_en,
            COALESCE(v_lineage, p_id), v_definition, v_tenant,
            NULL,
            NULL, NULL,
            NULL, NULL,
            v_detail_format_id, v_detail,
            p_reason_code, p_reason_message);
    END IF;

    RETURN QUERY SELECT 1 /* JobControlAction.Applied */::SMALLINT, v_from_status;
END;
$$;
