SELECT
    c.job_id,
    j.job_ref,
    ns.namespace_name,
    d.definition_name,
    c.checkpoint_name,
    {{decode:job-checkpoint-kind:c.checkpoint_kind_code}} AS checkpoint_kind,
    c.checkpoint_kind_code,
    {{decode:job-checkpoint-status:c.checkpoint_status_code}} AS checkpoint_status,
    c.checkpoint_status_code,
    strftime('%Y-%m-%dT%H:%M:%fZ', c.due_at_utc / 1000.0, 'unixepoch') AS due_at_utc,
    CASE c.value_format_id
        WHEN 0 /* JobPayloadFormat.None */ THEN 'none'
        WHEN 1 /* JobPayloadFormat.Json */ THEN 'json'
        WHEN 2 /* JobPayloadFormat.Bytes */ THEN 'bytes'
        ELSE 'custom-' || CAST(c.value_format_id AS TEXT)
    END AS value_format,
    CASE WHEN c.value_format_id = 1 /* JobPayloadFormat.Json */ THEN CAST(c.value AS TEXT) END AS value_text,
    strftime('%Y-%m-%dT%H:%M:%fZ', c.created_at_utc / 1000.0, 'unixepoch') AS created_at_utc,
    strftime('%Y-%m-%dT%H:%M:%fZ', c.modified_at_utc / 1000.0, 'unixepoch') AS modified_at_utc,
    c.version
FROM {{schema}}.checkpoints AS c
JOIN {{schema}}.jobs AS j ON j.job_id = c.job_id
JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = j.namespace_id
JOIN {{schema}}.definitions AS d ON d.definition_id = j.definition_id
