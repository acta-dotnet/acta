CREATE OR REPLACE FUNCTION {{schema}}.reschedule_job(
    p_id BIGINT,
    p_next_run_at_utc TIMESTAMPTZ,
    p_actor_code SMALLINT,
    p_actor_key VARCHAR,
    p_reason_code SMALLINT,
    p_reason_message VARCHAR
)
RETURNS TABLE (action SMALLINT, status_code SMALLINT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_from_status SMALLINT;
    v_namespace_id SMALLINT;
    v_lineage BIGINT;
    v_definition INT;
    v_tenant INT;
    v_en INT;
    v_audit SMALLINT;
    v_job_ref UUID;
BEGIN
    SELECT r.status_code, j.namespace_id, j.lineage_root_id, j.definition_id, j.tenant_id, r.execution_number, j.audit_level_code, j.job_ref
    INTO v_from_status, v_namespace_id, v_lineage, v_definition, v_tenant, v_en, v_audit, v_job_ref
    FROM {{schema}}.jobs j
    JOIN {{schema}}.runtimes r ON r.job_id = j.id
    WHERE j.id = p_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 2 /* ControlAction.NotFound */::SMALLINT, NULL::SMALLINT;
        RETURN;
    END IF;

    IF v_from_status NOT IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */) THEN
        RETURN QUERY SELECT 3 /* ControlAction.Rejected */::SMALLINT, v_from_status;
        RETURN;
    END IF;

    UPDATE {{schema}}.runtimes
    SET
        next_run_at_utc = p_next_run_at_utc,
        status_code = 10 /* JobStatusCode.Ready */,
        modified_at_utc = now(),
        version = version + 1
    WHERE job_id = p_id;

    IF v_audit = 20 /* JobAuditLevelCode.Audit */ THEN
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
            61 /* EventCode.JobRescheduled */,
            now(),
            v_namespace_id,
            p_actor_code,
            p_actor_key,
            p_id,
            v_job_ref,
            v_en,
            COALESCE(v_lineage, p_id),
            v_definition,
            v_tenant,
            NULL,
            v_from_status,
            10 /* JobStatusCode.Ready */,
            NULL,
            NULL,
            p_reason_code,
            p_reason_message);
    END IF;

    RETURN QUERY SELECT 1 /* ControlAction.Applied */::SMALLINT, 10 /* JobStatusCode.Ready */::SMALLINT;
END;
$$;
