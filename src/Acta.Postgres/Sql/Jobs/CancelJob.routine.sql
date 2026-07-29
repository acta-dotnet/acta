CREATE OR REPLACE FUNCTION {{schema}}.cancel_job(
    p_id             BIGINT,
    p_actor_code     SMALLINT,
    p_actor_key       VARCHAR,
    p_reason_code    SMALLINT,
    p_reason_message VARCHAR
)
RETURNS TABLE(action SMALLINT, status_code SMALLINT, parent_id BIGINT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_from_status       SMALLINT;
    v_namespace_id  SMALLINT;
    v_lineage_root_id   BIGINT;
    v_definition_id INT;
    v_tenant_id         INT;
    v_execution_number  INT;
    v_worker_id         INT;
    v_audit_level       SMALLINT;
    v_parent_id         BIGINT;
    v_job_ref           UUID;
    v_retention_seconds INT;
BEGIN
    SELECT r.status_code, j.namespace_id, j.lineage_root_id, j.definition_id, j.tenant_id, r.execution_number,
           r.leased_by_worker_id,
           j.audit_level_code, j.parent_id, j.job_ref
      INTO v_from_status, v_namespace_id, v_lineage_root_id, v_definition_id, v_tenant_id, v_execution_number, v_worker_id, v_audit_level, v_parent_id, v_job_ref
      FROM {{schema}}.jobs j
      JOIN {{schema}}.runtimes r ON r.job_id = j.id
     WHERE j.id = p_id
     FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 2 /* JobControlAction.NotFound */::SMALLINT, NULL::SMALLINT, NULL::BIGINT;
        RETURN;
    END IF;

    IF v_from_status NOT IN (
        30 /* JobStatusCode.Paused */,
        20 /* JobStatusCode.Suspended */,
        10 /* JobStatusCode.Ready */,
        40 /* JobStatusCode.Dispatched */,
        50 /* JobStatusCode.Executing */
    ) THEN
        RETURN QUERY SELECT 3 /* JobControlAction.Rejected */::SMALLINT, v_from_status, v_parent_id;
        RETURN;
    END IF;

    SELECT jd.retention_seconds_effective INTO v_retention_seconds
      FROM {{schema}}.definitions jd WHERE jd.id = v_definition_id;

    UPDATE {{schema}}.runtimes
       SET status_code          = 220 /* JobStatusCode.Cancelled */,
           leased_by_worker_id  = NULL,
           lease_expires_at_utc = NULL,
           retention_until_utc  = now() + make_interval(secs => v_retention_seconds),
           modified_at_utc      = now(),
           version              = version + 1
     WHERE job_id = p_id;

    IF v_audit_level = 20 /* JobAuditLevelCode.Audit */ THEN
        IF v_from_status = 50 /* JobStatusCode.Executing */ THEN
            INSERT INTO {{schema}}.events (
                event_code, created_at_utc, namespace_id,
                actor_code, actor_key,
                job_id, job_ref, execution_number,
                lineage_root_id, definition_id, tenant_id,
                worker_id,
                from_status_code, to_status_code,
                execution_status_code, duration_ms,
                reason_code, reason_message)
            VALUES (
                41 /* JobEventCode.JobExecutionFinished */, now(), v_namespace_id,
                p_actor_code, p_actor_key,
                p_id, v_job_ref, v_execution_number,
                COALESCE(v_lineage_root_id, p_id), v_definition_id, v_tenant_id,
                v_worker_id,
                50 /* JobStatusCode.Executing */, 220 /* JobStatusCode.Cancelled */,
                220 /* ExecutionStatusCode.Cancelled */, NULL,
                p_reason_code, p_reason_message);
        END IF;

        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id,
            actor_code, actor_key,
            job_id, job_ref, execution_number,
            lineage_root_id, definition_id, tenant_id,
            worker_id,
            from_status_code, to_status_code,
            execution_status_code, duration_ms,
            reason_code, reason_message)
        VALUES (
            70 /* JobEventCode.JobCancelled */, now(), v_namespace_id,
            p_actor_code, p_actor_key,
            p_id, v_job_ref, v_execution_number,
            COALESCE(v_lineage_root_id, p_id), v_definition_id, v_tenant_id,
            NULL,
            v_from_status, 220 /* JobStatusCode.Cancelled */,
            NULL, NULL,
            p_reason_code, p_reason_message);
    END IF;

    RETURN QUERY SELECT 1 /* JobControlAction.Applied */::SMALLINT, 220 /* JobStatusCode.Cancelled */::SMALLINT, v_parent_id;
END;
$$;
