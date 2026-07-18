INSERT INTO {{schema}}.definitions (
    namespace_id, name, status_code,
    input_type_name, output_type_name,
    input_format_id, input_format_name,
    output_format_id, output_format_name,
    priority_code, max_attempts,
    backoff,
    execution_timeout_seconds,
    deadline_seconds,
    deadline_behavior_code,
    retention_seconds,
    audit_level_code, alert_profile_code,
    alert_channel_name, runbook_url,
    display_name, description,
    definition_hash, manifest_generation_at_utc)
SELECT
    @p_namespace_id, json_extract(d.value, '$.name'), 10 /* JobDefinitionStatusCode.Active */,
    json_extract(d.value, '$.input_type_name'), json_extract(d.value, '$.output_type_name'),
    json_extract(d.value, '$.input_format_id'), json_extract(d.value, '$.input_format_name'),
    json_extract(d.value, '$.output_format_id'), json_extract(d.value, '$.output_format_name'),
    json_extract(d.value, '$.priority_code'), json_extract(d.value, '$.max_attempts'),
    json_extract(d.value, '$.backoff'),
    json_extract(d.value, '$.execution_timeout_seconds'),
    json_extract(d.value, '$.deadline_seconds'),
    json_extract(d.value, '$.deadline_behavior_code'),
    json_extract(d.value, '$.retention_seconds'),
    json_extract(d.value, '$.audit_level_code'), json_extract(d.value, '$.alert_profile_code'),
    json_extract(d.value, '$.alert_channel_name'), json_extract(d.value, '$.runbook_url'),
    json_extract(d.value, '$.display_name'), json_extract(d.value, '$.description'),
    json_extract(d.value, '$.definition_hash'), @p_manifest_generation
FROM json_each(@p_definitions) d
WHERE true
ON CONFLICT (namespace_id, name) DO UPDATE SET
    status_code = 10 /* JobDefinitionStatusCode.Active */,
    input_type_name = excluded.input_type_name,
    output_type_name = excluded.output_type_name,
    input_format_id = excluded.input_format_id,
    input_format_name = excluded.input_format_name,
    output_format_id = excluded.output_format_id,
    output_format_name = excluded.output_format_name,
    priority_code = excluded.priority_code,
    max_attempts = excluded.max_attempts,
    backoff = excluded.backoff,
    execution_timeout_seconds = excluded.execution_timeout_seconds,
    deadline_seconds = excluded.deadline_seconds,
    deadline_behavior_code = excluded.deadline_behavior_code,
    retention_seconds = excluded.retention_seconds,
    audit_level_code = excluded.audit_level_code,
    alert_profile_code = excluded.alert_profile_code,
    alert_channel_name = excluded.alert_channel_name,
    runbook_url = excluded.runbook_url,
    display_name = excluded.display_name,
    description = excluded.description,
    definition_hash = excluded.definition_hash,
    manifest_generation_at_utc = excluded.manifest_generation_at_utc,
    modified_at_utc = {{now}},
    version = {{schema}}.definitions.version + 1
  WHERE excluded.manifest_generation_at_utc >= {{schema}}.definitions.manifest_generation_at_utc
    AND (
         {{schema}}.definitions.status_code <> 10 /* JobDefinitionStatusCode.Active */
      OR {{schema}}.definitions.definition_hash IS NOT excluded.definition_hash
    );

UPDATE {{schema}}.definitions
   SET status_code = 240 /* JobDefinitionStatusCode.Retired */,
       modified_at_utc = {{now}},
       version = version + 1
 WHERE namespace_id = @p_namespace_id
   AND status_code = 10 /* JobDefinitionStatusCode.Active */
   AND manifest_generation_at_utc <= @p_manifest_generation
   AND name NOT IN (SELECT json_extract(d.value, '$.name') FROM json_each(@p_definitions) d);

SELECT json_extract(d.value, '$.name') AS def_name, jd.id AS def_id
  FROM json_each(@p_definitions) d
  JOIN {{schema}}.definitions jd
    ON jd.namespace_id = @p_namespace_id
   AND jd.name = json_extract(d.value, '$.name');
