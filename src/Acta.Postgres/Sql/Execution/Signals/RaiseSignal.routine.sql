CREATE OR REPLACE FUNCTION {{schema}}.raise_signal(
    p_job_id BIGINT,
    p_kind_code SMALLINT,
    p_name VARCHAR,
    p_value_format_id SMALLINT,
    p_value BYTEA,
    p_actor_code SMALLINT,
    p_actor_key VARCHAR,
    p_reason_code SMALLINT,
    p_reason_message VARCHAR
)
RETURNS TABLE (action SMALLINT, status_code SMALLINT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_now TIMESTAMPTZ := now();
    v_existing SMALLINT;
    v_from_status SMALLINT;
    v_namespace_id SMALLINT;
    v_lineage_root_id BIGINT;
    v_definition_id INT;
    v_tenant_id INT;
    v_execution_number INT;
    v_audit_level SMALLINT;
    v_job_ref UUID;
BEGIN

    SELECT js.status_code INTO v_existing
    FROM {{schema}}.checkpoints js
    WHERE
        js.job_id = p_job_id
        AND js.kind_code = p_kind_code
        AND js.name = p_name
    FOR UPDATE;

    SELECT
        r.status_code,
        j.namespace_id,
        j.lineage_root_id,
        j.definition_id,
        j.tenant_id,
        r.execution_number,
        j.audit_level_code,
        j.job_ref
    INTO v_from_status, v_namespace_id, v_lineage_root_id, v_definition_id, v_tenant_id, v_execution_number, v_audit_level, v_job_ref
    FROM {{schema}}.runtimes r
    JOIN {{schema}}.jobs j ON j.id = r.job_id
    WHERE r.job_id = p_job_id
    FOR UPDATE OF r;

    IF v_from_status IS NULL THEN
        RETURN QUERY SELECT 2 /* ControlAction.NotFound */::SMALLINT, NULL::SMALLINT;
        RETURN;
    END IF;

    IF v_from_status IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */) THEN
        RETURN QUERY SELECT 3 /* ControlAction.Rejected */::SMALLINT, v_from_status;
        RETURN;
    END IF;

    INSERT INTO {{schema}}.checkpoints (
        job_id,
        kind_code,
        name,
        status_code,
        value_format_id,
        value,
        created_at_utc,
        modified_at_utc,
        version)
    VALUES (
        p_job_id,
        p_kind_code,
        p_name,
        20 /* JobCheckpointStatusCode.Set */,
        p_value_format_id,
        p_value,
        v_now,
        v_now,
        0)
    ON CONFLICT (job_id, kind_code, name) DO UPDATE SET
        status_code = 20 /* JobCheckpointStatusCode.Set */,
        value_format_id = EXCLUDED.value_format_id,
        value = EXCLUDED.value,
        modified_at_utc = v_now,
        version = {{schema}}.checkpoints.version + 1;

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
            80 /* EventCode.JobSignalRaised */,
            v_now,
            v_namespace_id,
            p_actor_code,
            p_actor_key,
            p_job_id,
            v_job_ref,
            v_execution_number,
            COALESCE(v_lineage_root_id, p_job_id),
            v_definition_id,
            v_tenant_id,
            NULL,
            v_from_status,
            v_from_status,
            NULL,
            NULL,
            p_reason_code,
            p_reason_message);
    END IF;

    IF v_from_status = 20 /* JobStatusCode.Suspended */ THEN
        UPDATE {{schema}}.runtimes
        SET
            status_code = 10 /* JobStatusCode.Ready */,
            next_run_at_utc = v_now,
            modified_at_utc = v_now,
            version = version + 1
        WHERE job_id = p_job_id;

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
                72 /* EventCode.JobResumed */,
                v_now,
                v_namespace_id,
                p_actor_code,
                p_actor_key,
                p_job_id,
                v_job_ref,
                v_execution_number,
                COALESCE(v_lineage_root_id, p_job_id),
                v_definition_id,
                v_tenant_id,
                NULL,
                20 /* JobStatusCode.Suspended */,
                10 /* JobStatusCode.Ready */,
                NULL,
                NULL,
                p_reason_code,
                p_reason_message);
        END IF;

        RETURN QUERY SELECT 1 /* ControlAction.Applied */::SMALLINT, 10 /* JobStatusCode.Ready */::SMALLINT;
        RETURN;
    END IF;

    RETURN QUERY SELECT 1 /* ControlAction.Applied */::SMALLINT, v_from_status;
END;
$$;
