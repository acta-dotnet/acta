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
    firing_failure_policy_code,
    scheduled_delivery_code,
    default_input_format_id,
    default_input,
    tenant_requirement_code,
    alert_channel_name,
    runbook_url,
    display_name,
    description,
    definition_hash,
    manifest_generation_at_utc)
SELECT
    @p_namespace_id,
    json_extract(d.value, '$.definition_name'),
    10 /* DefinitionStatusCode.Active */,
    json_extract(d.value, '$.input_type_name'),
    json_extract(d.value, '$.output_type_name'),
    json_extract(d.value, '$.input_format_id'),
    json_extract(d.value, '$.input_format_name'),
    json_extract(d.value, '$.output_format_id'),
    json_extract(d.value, '$.output_format_name'),
    json_extract(d.value, '$.job_priority_code'),
    json_extract(d.value, '$.max_attempts'),
    json_extract(d.value, '$.concurrency_limit'),
    json_extract(d.value, '$.rate_limit'),
    json_extract(d.value, '$.rate_name'),
    json_extract(d.value, '$.lane_name'),
    json_extract(d.value, '$.backoff'),
    json_extract(d.value, '$.execution_timeout_seconds'),
    json_extract(d.value, '$.deadline_seconds'),
    json_extract(d.value, '$.deadline_behavior_code'),
    json_extract(d.value, '$.retention_seconds'),
    json_extract(d.value, '$.event_recording_code'),
    json_extract(d.value, '$.alert_profile_code'),
    json_extract(d.value, '$.firing_failure_policy_code'),
    json_extract(d.value, '$.scheduled_delivery_code'),
    json_extract(d.value, '$.default_input_format_id'),
    CASE
        WHEN json_extract(d.value, '$.default_input_format_id') <> 0 /* JobPayloadFormat.None */
            THEN COALESCE(acta_blob(json_extract(d.value, '$.default_input')), X'')
    END,
    json_extract(d.value, '$.tenant_requirement_code'),
    json_extract(d.value, '$.alert_channel_name'),
    json_extract(d.value, '$.runbook_url'),
    json_extract(d.value, '$.display_name'),
    json_extract(d.value, '$.description'),
    json_extract(d.value, '$.definition_hash'),
    @p_manifest_generation
FROM json_each(@p_definitions) d
WHERE true
ON CONFLICT (namespace_id, definition_name) DO UPDATE SET
    definition_status_code = 10 /* DefinitionStatusCode.Active */,
    input_type_name = excluded.input_type_name,
    output_type_name = excluded.output_type_name,
    input_format_id = excluded.input_format_id,
    input_format_name = excluded.input_format_name,
    output_format_id = excluded.output_format_id,
    output_format_name = excluded.output_format_name,
    job_priority_code = excluded.job_priority_code,
    max_attempts = excluded.max_attempts,
    concurrency_limit = excluded.concurrency_limit,
    rate_limit = excluded.rate_limit,
    rate_name = excluded.rate_name,
    lane_name = excluded.lane_name,
    backoff = excluded.backoff,
    execution_timeout_seconds = excluded.execution_timeout_seconds,
    deadline_seconds = excluded.deadline_seconds,
    deadline_behavior_code = excluded.deadline_behavior_code,
    retention_seconds = excluded.retention_seconds,
    event_recording_code = excluded.event_recording_code,
    alert_profile_code = excluded.alert_profile_code,
    firing_failure_policy_code = excluded.firing_failure_policy_code,
    scheduled_delivery_code = excluded.scheduled_delivery_code,
    default_input_format_id = excluded.default_input_format_id,
    default_input = excluded.default_input,
    tenant_requirement_code = excluded.tenant_requirement_code,
    alert_channel_name = excluded.alert_channel_name,
    runbook_url = excluded.runbook_url,
    display_name = excluded.display_name,
    description = excluded.description,
    definition_hash = excluded.definition_hash,
    manifest_generation_at_utc = excluded.manifest_generation_at_utc,
    -- An unchanged Active row from a newer manifest takes only the generation, because an equal hash means the columns
    -- above already hold these values; version and modified_at_utc stay, so no operator CAS fails and no worker reloads.
    modified_at_utc = CASE WHEN {{schema}}.definitions.definition_status_code <> 10 /* DefinitionStatusCode.Active */
        OR {{schema}}.definitions.definition_hash IS NOT excluded.definition_hash
        THEN {{now}} ELSE {{schema}}.definitions.modified_at_utc END,
    version = CASE WHEN {{schema}}.definitions.definition_status_code <> 10 /* DefinitionStatusCode.Active */
        OR {{schema}}.definitions.definition_hash IS NOT excluded.definition_hash
        THEN {{schema}}.definitions.version + 1 ELSE {{schema}}.definitions.version END
WHERE
    excluded.manifest_generation_at_utc >= {{schema}}.definitions.manifest_generation_at_utc
    AND (
        {{schema}}.definitions.definition_status_code <> 10 /* DefinitionStatusCode.Active */
        OR {{schema}}.definitions.definition_hash IS NOT excluded.definition_hash
        -- An unchanged row still takes a newer generation, so an older build that registers after it is refused.
        OR excluded.manifest_generation_at_utc > {{schema}}.definitions.manifest_generation_at_utc
    );

-- CROSS JOIN keeps the batch the outer loop, one index seek per name: a plain join lets the planner walk the
-- namespace's definitions and re-read the whole JSON batch for each.
SELECT json_extract(d.value, '$.definition_name') AS def_name, jd.definition_id AS def_id
FROM json_each(@p_definitions) d
CROSS JOIN {{schema}}.definitions jd
    ON jd.namespace_id = @p_namespace_id
    AND jd.definition_name = json_extract(d.value, '$.definition_name');
