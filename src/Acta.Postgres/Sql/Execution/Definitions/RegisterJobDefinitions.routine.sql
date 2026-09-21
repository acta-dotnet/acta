CREATE OR REPLACE FUNCTION {{schema}}.register_job_definitions(
    p_namespace_id INT,
    p_manifest_generation TIMESTAMPTZ,
    p_d_name VARCHAR [],
    p_d_priority_code SMALLINT [],
    p_d_max_attempts SMALLINT [],
    p_d_concurrency_limit SMALLINT [],
    p_d_rate_limit VARCHAR [],
    p_d_rate_key VARCHAR [],
    p_d_backoff VARCHAR [],
    p_d_execution_timeout INT [],
    p_d_deadline_seconds INT [],
    p_d_deadline_behavior SMALLINT [],
    p_d_job_retention INT [],
    p_d_input_type_name VARCHAR [],
    p_d_output_type_name VARCHAR [],
    p_d_input_format_id SMALLINT [],
    p_d_input_format_name VARCHAR [],
    p_d_output_format_id SMALLINT [],
    p_d_output_format_name VARCHAR [],
    p_d_audit_level_code SMALLINT [],
    p_d_alert_profile_code SMALLINT [],
    p_d_tenant_requirement SMALLINT [],
    p_d_alert_channel_name VARCHAR [],
    p_d_runbook_url VARCHAR [],
    p_d_display_name VARCHAR [],
    p_d_description VARCHAR [],
    p_d_definition_hash VARCHAR []
)

RETURNS TABLE (def_name VARCHAR, def_id INT)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    WITH batch AS (
        SELECT * FROM unnest(
            p_d_name, p_d_priority_code, p_d_max_attempts, p_d_concurrency_limit,
            p_d_rate_limit, p_d_rate_key,
            p_d_backoff,
            p_d_execution_timeout, p_d_deadline_seconds, p_d_deadline_behavior, p_d_job_retention,
            p_d_input_type_name, p_d_output_type_name,
            p_d_input_format_id, p_d_input_format_name, p_d_output_format_id, p_d_output_format_name,
            p_d_audit_level_code, p_d_alert_profile_code, p_d_tenant_requirement,
            p_d_alert_channel_name, p_d_runbook_url, p_d_display_name, p_d_description, p_d_definition_hash
        ) AS b(name, priority_code, max_attempts, concurrency_limit,
            rate_limit, rate_key,
            backoff,
            execution_timeout_seconds, deadline_seconds,
            deadline_behavior_code, retention_seconds,
            input_type_name, output_type_name,
            input_format_id, input_format_name, output_format_id, output_format_name,
            audit_level_code, alert_profile_code, tenant_requirement_code,
            alert_channel_name, runbook_url, display_name, description, definition_hash)
    ),
    upserted AS (
        INSERT INTO {{schema}}.definitions (
            namespace_id,
            name,
            status_code,
            input_type_name,
            output_type_name,
            input_format_id,
            input_format_name,
            output_format_id,
            output_format_name,
            priority_code,
            max_attempts,
            concurrency_limit,
            rate_limit,
            rate_key,
            backoff,
            execution_timeout_seconds,
            deadline_seconds,
            deadline_behavior_code,
            retention_seconds,
            audit_level_code,
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
            p_namespace_id,
            b.name,
            10 /* JobDefinitionStatusCode.Active */,
            b.input_type_name,
            b.output_type_name,
            b.input_format_id,
            b.input_format_name,
            b.output_format_id,
            b.output_format_name,
            b.priority_code,
            b.max_attempts,
            b.concurrency_limit,
            b.rate_limit,
            b.rate_key,
            b.backoff,
            b.execution_timeout_seconds,
            b.deadline_seconds,
            b.deadline_behavior_code,
            b.retention_seconds,
            b.audit_level_code,
            b.alert_profile_code,
            b.tenant_requirement_code,
            b.alert_channel_name,
            b.runbook_url,
            b.display_name,
            b.description,
            b.definition_hash,
            p_manifest_generation,
            now(),
            now(),
            0
        FROM batch b
        ON CONFLICT (namespace_id, name) DO UPDATE SET
            status_code = 10 /* JobDefinitionStatusCode.Active */,
            input_type_name = EXCLUDED.input_type_name,
            output_type_name = EXCLUDED.output_type_name,
            input_format_id = EXCLUDED.input_format_id,
            input_format_name = EXCLUDED.input_format_name,
            output_format_id = EXCLUDED.output_format_id,
            output_format_name = EXCLUDED.output_format_name,
            priority_code = EXCLUDED.priority_code,
            max_attempts = EXCLUDED.max_attempts,
            concurrency_limit = EXCLUDED.concurrency_limit,
            rate_limit = EXCLUDED.rate_limit,
            rate_key = EXCLUDED.rate_key,
            backoff = EXCLUDED.backoff,
            execution_timeout_seconds = EXCLUDED.execution_timeout_seconds,
            deadline_seconds = EXCLUDED.deadline_seconds,
            deadline_behavior_code = EXCLUDED.deadline_behavior_code,
            retention_seconds = EXCLUDED.retention_seconds,
            audit_level_code = EXCLUDED.audit_level_code,
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
                {{schema}}.definitions.status_code <> 10 /* JobDefinitionStatusCode.Active */
                OR {{schema}}.definitions.definition_hash IS DISTINCT FROM EXCLUDED.definition_hash
            )
        RETURNING definitions.name, definitions.id
    )
    SELECT u.name, u.id FROM upserted u
    UNION ALL
    SELECT b.name, jd.id
    FROM batch b
    INNER JOIN {{schema}}.definitions jd
        ON jd.namespace_id = p_namespace_id AND jd.name = b.name
    WHERE NOT EXISTS (SELECT 1 FROM upserted u WHERE u.name = b.name);
END;
$$;

-- CREATE OR REPLACE across arities creates an overload instead of replacing; drop the retired
-- signature (without rate_limit and rate_key) so pre-existing installs cannot resolve the stale form.
DROP FUNCTION IF EXISTS {{schema}}.register_job_definitions(
    INT, TIMESTAMPTZ, VARCHAR [], SMALLINT [], SMALLINT [], SMALLINT [], VARCHAR [], INT [], INT [], SMALLINT [], INT [],
    VARCHAR [], VARCHAR [], SMALLINT [], VARCHAR [], SMALLINT [], VARCHAR [], SMALLINT [], SMALLINT [], SMALLINT [],
    VARCHAR [], VARCHAR [], VARCHAR [], VARCHAR [], VARCHAR []
);
