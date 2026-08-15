CREATE OR REPLACE FUNCTION {{schema}}.acknowledge_job_alert(
    p_alert_ref UUID,
    p_actor_code SMALLINT,
    p_actor_key VARCHAR,
    p_reason_message VARCHAR
)
RETURNS TABLE (out_action SMALLINT, out_acknowledged_at_utc TIMESTAMPTZ, out_resolved_at_utc TIMESTAMPTZ)
LANGUAGE plpgsql
AS $$
DECLARE
    v_namespace_id SMALLINT;
    v_job_id BIGINT;
    v_job_ref UUID;
    v_ack TIMESTAMPTZ;
    v_resolved TIMESTAMPTZ;
    v_definition_id INT;
    v_lineage_root_id BIGINT;
    v_execution_number INT;
BEGIN
    SELECT a.namespace_id, a.job_id, a.job_ref, a.acknowledged_at_utc, a.resolved_at_utc
    INTO v_namespace_id, v_job_id, v_job_ref, v_ack, v_resolved
    FROM {{schema}}.alerts a
    WHERE a.alert_ref = p_alert_ref
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 2 /* ControlAction.NotFound */::SMALLINT, NULL::TIMESTAMPTZ, NULL::TIMESTAMPTZ;
        RETURN;
    END IF;

    IF v_ack IS NOT NULL THEN
        RETURN QUERY SELECT 1 /* ControlAction.Applied */::SMALLINT, v_ack, v_resolved;
        RETURN;
    END IF;

    SELECT j.definition_id, j.lineage_root_id INTO v_definition_id, v_lineage_root_id
    FROM {{schema}}.jobs j
    WHERE j.id = v_job_id;
    SELECT r.execution_number INTO v_execution_number
    FROM {{schema}}.runtimes r
    WHERE r.job_id = v_job_id;

    v_ack := now();

    UPDATE {{schema}}.alerts
    SET
        acknowledged_at_utc = v_ack,
        modified_at_utc = now(),
        version = version + 1
    WHERE alert_ref = p_alert_ref;

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
        140 /* EventCode.AlertAcknowledged */,
        now(),
        v_namespace_id,
        p_actor_code,
        p_actor_key,
        v_job_id,
        v_job_ref,
        v_execution_number,
        COALESCE(v_lineage_root_id, v_job_id),
        v_definition_id,
        NULL,
        NULL,
        NULL,
        NULL,
        NULL,
        NULL,
        p_reason_message);

    RETURN QUERY SELECT 1 /* ControlAction.Applied */::SMALLINT, v_ack, v_resolved;
END;
$$;
