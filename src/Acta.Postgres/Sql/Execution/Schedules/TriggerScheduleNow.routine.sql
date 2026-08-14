CREATE OR REPLACE FUNCTION {{schema}}.trigger_schedule_now(
    p_job_id BIGINT,
    p_name VARCHAR,
    p_actor_code SMALLINT,
    p_actor_key VARCHAR,
    p_reason_message VARCHAR
)
RETURNS TABLE (
    out_action SMALLINT, out_status_code SMALLINT, out_paused_until_utc TIMESTAMPTZ, out_next_run_at_utc TIMESTAMPTZ, out_version INT
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_schedule_id BIGINT;
    v_status SMALLINT;
    v_paused TIMESTAMPTZ;
    v_next TIMESTAMPTZ;
    v_version INT;
    v_slot_status SMALLINT;
    v_ns SMALLINT;
    v_def INT;
    v_lineage BIGINT;
    v_en INT;
    v_audit SMALLINT;
    v_job_ref UUID;
BEGIN
    /* Lock the slot's runtimes row before the schedules row: register_scheduled_jobs writes
       runtimes then schedules, so every writer of both must take runtimes first. The schedules row
       is guard-only here (never updated): a manual trigger moves only the slot's cursor. */
    SELECT r.status_code, r.execution_number
    INTO v_slot_status, v_en
    FROM {{schema}}.runtimes r
    WHERE r.job_id = p_job_id
    FOR UPDATE;

    SELECT js.id, js.status_code, js.paused_until_utc, js.next_run_at_utc, js.version
    INTO v_schedule_id, v_status, v_paused, v_next, v_version
    FROM {{schema}}.schedules js
    WHERE
        js.job_id = p_job_id
        AND js.name = p_name
        AND js.status_code <> 230 /* ScheduleStatusCode.Orphaned */
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 2 /* JobControlAction.NotFound */::SMALLINT, NULL::SMALLINT, NULL::TIMESTAMPTZ, NULL::TIMESTAMPTZ, NULL::INT;
        RETURN;
    END IF;

    IF v_status = 30 /* ScheduleStatusCode.Paused */ THEN
        RETURN QUERY SELECT 3 /* JobControlAction.Rejected */::SMALLINT, v_status, v_paused, v_next, v_version;
        RETURN;
    END IF;

    IF v_slot_status IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */) THEN
        RETURN QUERY SELECT 3 /* JobControlAction.Rejected */::SMALLINT, v_status, v_paused, v_next, v_version;
        RETURN;
    END IF;

    IF v_slot_status IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */) THEN
        RETURN QUERY SELECT 3 /* JobControlAction.Rejected */::SMALLINT, v_status, v_paused, v_next, v_version;
        RETURN;
    END IF;

    SELECT j.namespace_id, j.definition_id, j.lineage_root_id, j.audit_level_code, j.job_ref
    INTO v_ns, v_def, v_lineage, v_audit, v_job_ref
    FROM {{schema}}.jobs j
    WHERE j.id = p_job_id;

    UPDATE {{schema}}.runtimes
    SET
        next_run_at_utc = now(),
        status_code = 10 /* JobStatusCode.Ready */,
        modified_at_utc = now(),
        version = version + 1
    WHERE
        job_id = p_job_id
        AND status_code IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */);

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
            worker_id,
            from_status_code,
            to_status_code,
            execution_status_code,
            duration_ms,
            reason_code,
            reason_message)
        VALUES (
            104 /* EventCode.ScheduleTriggered */,
            now(),
            v_ns,
            p_actor_code,
            p_actor_key,
            p_job_id,
            v_job_ref,
            v_en,
            COALESCE(v_lineage, p_job_id),
            v_def,
            NULL,
            NULL,
            NULL,
            NULL,
            NULL,
            NULL,
            p_reason_message);
    END IF;

    RETURN QUERY SELECT 1 /* JobControlAction.Applied */::SMALLINT, v_status, v_paused, v_next, v_version;
END;
$$;
