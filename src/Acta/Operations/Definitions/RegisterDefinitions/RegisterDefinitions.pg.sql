WITH batch AS (
    SELECT * FROM unnest(
        @p_d_definition_name, @p_d_job_priority_code, @p_d_max_attempts, @p_d_concurrency_limit,
        @p_d_rate_limit, @p_d_rate_name,
        @p_d_backoff,
        @p_d_execution_timeout, @p_d_deadline_seconds, @p_d_deadline_behavior, @p_d_job_retention,
        @p_d_input_type_name, @p_d_output_type_name,
        @p_d_input_format_id, @p_d_input_format_name, @p_d_output_format_id, @p_d_output_format_name,
        @p_d_event_recording_code, @p_d_alert_profile_code, @p_d_tenant_requirement,
        @p_d_alert_channel_name, @p_d_runbook_url, @p_d_display_name, @p_d_description, @p_d_definition_hash,
        @p_d_lane_name
    ) AS b(definition_name, job_priority_code, max_attempts, concurrency_limit,
        rate_limit, rate_name,
        backoff,
        execution_timeout_seconds, deadline_seconds,
        deadline_behavior_code, retention_seconds,
        input_type_name, output_type_name,
        input_format_id, input_format_name, output_format_id, output_format_name,
        event_recording_code, alert_profile_code, tenant_requirement_code,
        alert_channel_name, runbook_url, display_name, description, definition_hash, lane_name)
),
upserted AS (
    INSERT INTO {{schema}}.definitions (
        namespace_id,
        definition_name,
        definition_status_code,
        input_type_name,
        output_type_name,
        input_format_id,
        input_format_name,
        output_format_id,
        output_format_name,
        job_priority_code,
        max_attempts,
        concurrency_limit,
        rate_limit,
        rate_name,
        lane_name,
        backoff,
        execution_timeout_seconds,
        deadline_seconds,
        deadline_behavior_code,
        retention_seconds,
        event_recording_code,
        alert_profile_code,
        tenant_requirement_code,
        alert_channel_name,
        runbook_url,
        display_name,
        description,
        definition_hash,
        manifest_generation_at_utc,
        created_at_utc,
        modified_at_utc,
        version)
    SELECT
        @p_namespace_id,
        b.definition_name,
        10 /* DefinitionStatusCode.Active */,
        b.input_type_name,
        b.output_type_name,
        b.input_format_id,
        b.input_format_name,
        b.output_format_id,
        b.output_format_name,
        b.job_priority_code,
        b.max_attempts,
        b.concurrency_limit,
        b.rate_limit,
        b.rate_name,
        b.lane_name,
        b.backoff,
        b.execution_timeout_seconds,
        b.deadline_seconds,
        b.deadline_behavior_code,
        b.retention_seconds,
        b.event_recording_code,
        b.alert_profile_code,
        b.tenant_requirement_code,
        b.alert_channel_name,
        b.runbook_url,
        b.display_name,
        b.description,
        b.definition_hash,
        @p_manifest_generation,
        now(),
        now(),
        0
    FROM batch b
    ON CONFLICT (namespace_id, definition_name) DO UPDATE SET
        definition_status_code = 10 /* DefinitionStatusCode.Active */,
        input_type_name = EXCLUDED.input_type_name,
        output_type_name = EXCLUDED.output_type_name,
        input_format_id = EXCLUDED.input_format_id,
        input_format_name = EXCLUDED.input_format_name,
        output_format_id = EXCLUDED.output_format_id,
        output_format_name = EXCLUDED.output_format_name,
        job_priority_code = EXCLUDED.job_priority_code,
        max_attempts = EXCLUDED.max_attempts,
        concurrency_limit = EXCLUDED.concurrency_limit,
        rate_limit = EXCLUDED.rate_limit,
        rate_name = EXCLUDED.rate_name,
        lane_name = EXCLUDED.lane_name,
        backoff = EXCLUDED.backoff,
        execution_timeout_seconds = EXCLUDED.execution_timeout_seconds,
        deadline_seconds = EXCLUDED.deadline_seconds,
        deadline_behavior_code = EXCLUDED.deadline_behavior_code,
        retention_seconds = EXCLUDED.retention_seconds,
        event_recording_code = EXCLUDED.event_recording_code,
        alert_profile_code = EXCLUDED.alert_profile_code,
        tenant_requirement_code = EXCLUDED.tenant_requirement_code,
        alert_channel_name = EXCLUDED.alert_channel_name,
        runbook_url = EXCLUDED.runbook_url,
        display_name = EXCLUDED.display_name,
        description = EXCLUDED.description,
        definition_hash = EXCLUDED.definition_hash,
        manifest_generation_at_utc = EXCLUDED.manifest_generation_at_utc,
        modified_at_utc = now(),
        version = {{schema}}.definitions.version + 1
    WHERE
        EXCLUDED.manifest_generation_at_utc >= {{schema}}.definitions.manifest_generation_at_utc
        AND (
            {{schema}}.definitions.definition_status_code <> 10 /* DefinitionStatusCode.Active */
            OR {{schema}}.definitions.definition_hash IS DISTINCT FROM EXCLUDED.definition_hash
        )
    RETURNING definitions.definition_name, definitions.definition_id
)
SELECT u.definition_name AS def_name, u.definition_id AS def_id FROM upserted u
UNION ALL
SELECT b.definition_name AS def_name, jd.definition_id AS def_id
FROM batch b
INNER JOIN {{schema}}.definitions jd
    ON jd.namespace_id = @p_namespace_id AND jd.definition_name = b.definition_name
WHERE NOT EXISTS (SELECT 1 FROM upserted u WHERE u.definition_name = b.definition_name);
