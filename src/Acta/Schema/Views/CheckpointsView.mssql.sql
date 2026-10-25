SELECT
    c.job_id,
    j.job_ref,
    ns.namespace_name AS namespace,
    d.definition_name AS job_name,
    c.checkpoint_name,
    {{decode:job-checkpoint-kind:c.checkpoint_kind_code}} AS kind,
    c.checkpoint_kind_code,
    {{decode:job-checkpoint-status:c.checkpoint_status_code}} AS status,
    c.checkpoint_status_code,
    c.due_at_utc,
    CASE c.value_format_id
        WHEN 0 /* JobPayloadFormat.None */ THEN 'none'
        WHEN 1 /* JobPayloadFormat.Json */ THEN 'json'
        WHEN 2 /* JobPayloadFormat.Bytes */ THEN 'bytes'
        ELSE CONCAT('custom-', c.value_format_id)
    END AS value_format,
    CASE
        WHEN
            c.value_format_id = 1 /* JobPayloadFormat.Json */
            THEN CAST(c.value AS VARCHAR(MAX)) COLLATE latin1_general_100_bin2_utf8
    END AS value_text,
    c.created_at_utc,
    c.modified_at_utc,
    c.version
FROM {{schema}}.checkpoints AS c
JOIN {{schema}}.jobs AS j ON j.job_id = c.job_id
JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = j.namespace_id
JOIN {{schema}}.definitions AS d ON d.definition_id = j.definition_id
