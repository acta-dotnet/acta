CREATE OR REPLACE FUNCTION {{schema}}.pause_job(
    p_id BIGINT,
    p_actor_code SMALLINT,
    p_actor_key VARCHAR,
    p_reason_code SMALLINT,
    p_reason_message VARCHAR,
    p_expected_version INT DEFAULT NULL
)
RETURNS TABLE (action SMALLINT, status_code SMALLINT, version INT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_from_status SMALLINT;
    v_version INT;
    v_namespace_id INT;
    v_lineage_root_id BIGINT;
    v_definition_id INT;
    v_tenant_id INT;
    v_execution_number INT;
    v_audit_level SMALLINT;
    v_job_ref UUID;
BEGIN
    SELECT r.status_code, j.namespace_id, j.lineage_root_id, j.definition_id, j.tenant_id, r.execution_number, j.audit_level_code, j.job_ref, r.version
    INTO v_from_status, v_namespace_id, v_lineage_root_id, v_definition_id, v_tenant_id, v_execution_number, v_audit_level, v_job_ref, v_version
    FROM {{schema}}.runtimes r
    JOIN {{schema}}.jobs j ON j.id = r.job_id
    WHERE r.job_id = p_id
    FOR UPDATE OF r;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 2 /* ControlAction.NotFound */::SMALLINT, NULL::SMALLINT, NULL::INT;
        RETURN;
    END IF;

    IF p_expected_version IS NOT NULL AND v_version <> p_expected_version THEN
        RETURN QUERY SELECT 5 /* ControlAction.VersionConflict */::SMALLINT, v_from_status, v_version;
        RETURN;
    END IF;

    IF v_from_status NOT IN (
        30 /* JobStatusCode.Paused */,
        20 /* JobStatusCode.Suspended */,
        10 /* JobStatusCode.Ready */
    ) THEN
        RETURN QUERY SELECT 3 /* ControlAction.Rejected */::SMALLINT, v_from_status, v_version;
        RETURN;
    END IF;

    -- Aliased and qualified: the RETURNS TABLE output column named version would otherwise make the
    -- bare column reference ambiguous. Same in every job control routine that returns the version.
    UPDATE {{schema}}.runtimes AS r
    SET
        status_code = 30 /* JobStatusCode.Paused */,
        modified_at_utc = now(),
        version = r.version + 1
    WHERE r.job_id = p_id;

    IF v_audit_level = 20 /* JobAuditLevelCode.Audit */ THEN
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
        VALUES (
            71 /* EventCode.JobPaused */,
            now(),
            v_namespace_id,
            p_actor_code,
            p_actor_key,
            p_id,
            v_job_ref,
            v_execution_number,
            COALESCE(v_lineage_root_id, p_id),
            v_definition_id,
            v_tenant_id,
            NULL,
            v_from_status,
            30 /* JobStatusCode.Paused */,
            NULL,
            NULL,
            p_reason_code,
            p_reason_message);
    END IF;

    RETURN QUERY SELECT 1 /* ControlAction.Applied */::SMALLINT, 30 /* JobStatusCode.Paused */::SMALLINT, v_version + 1;
END;
$$;
