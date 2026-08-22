CREATE OR REPLACE FUNCTION {{schema}}.set_job_definition_overrides(
    p_id INT,
    p_version INT,
    p_priority_code_override SMALLINT,
    p_max_attempts_override SMALLINT,
    p_backoff_override VARCHAR,
    p_execution_timeout_seconds_override INT,
    p_deadline_seconds_override INT,
    p_deadline_behavior_code_override SMALLINT,
    p_retention_seconds_override INT,
    p_audit_level_code_override SMALLINT,
    p_alert_profile_code_override SMALLINT,
    p_alert_channel_name_override VARCHAR,
    p_runbook_url_override VARCHAR,
    p_display_name_override VARCHAR,
    p_description_override VARCHAR,
    p_actor_code SMALLINT,
    p_actor_key VARCHAR,
    p_reason_code SMALLINT,
    p_reason_message VARCHAR
)
RETURNS TABLE (action SMALLINT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_ns INT;
    v_version INT;
BEGIN
    SELECT jd.namespace_id, jd.version INTO v_ns, v_version
    FROM {{schema}}.definitions jd WHERE jd.id = p_id;

    IF v_ns IS NULL THEN
        RETURN QUERY SELECT 2 /* DefinitionOverrideAction.NotFound */::SMALLINT;
        RETURN;
    END IF;

    IF v_version <> p_version THEN
        RETURN QUERY SELECT 3 /* DefinitionOverrideAction.VersionConflict */::SMALLINT;
        RETURN;
    END IF;

    UPDATE {{schema}}.definitions
    SET
        priority_code_override = p_priority_code_override,
        max_attempts_override = p_max_attempts_override,
        backoff_override = p_backoff_override,
        execution_timeout_seconds_override = p_execution_timeout_seconds_override,
        deadline_seconds_override = p_deadline_seconds_override,
        deadline_behavior_code_override = p_deadline_behavior_code_override,
        retention_seconds_override = p_retention_seconds_override,
        audit_level_code_override = p_audit_level_code_override,
        alert_profile_code_override = p_alert_profile_code_override,
        alert_channel_name_override = p_alert_channel_name_override,
        runbook_url_override = p_runbook_url_override,
        display_name_override = p_display_name_override,
        description_override = p_description_override,
        modified_at_utc = now(),
        version = version + 1
    WHERE id = p_id;

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
        30 /* EventCode.JobDefinitionOverridesUpdated */,
        now(),
        v_ns,
        p_actor_code,
        p_actor_key,
        NULL,
        NULL,
        NULL,
        NULL,
        p_id,
        NULL,
        NULL,
        NULL,
        NULL,
        NULL,
        p_reason_code,
        p_reason_message);

    RETURN QUERY SELECT 1 /* DefinitionOverrideAction.Applied */::SMALLINT;
END;
$$;
