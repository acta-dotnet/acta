SELECT
    c.job_id,
    j.job_ref,
    ns.name AS namespace,
    d.name AS job_name,
    c.name AS checkpoint_name,
    {{decode:job-checkpoint-kind:c.kind_code}} AS kind,
    c.kind_code,
    {{decode:job-checkpoint-status:c.status_code}} AS state,
    c.status_code,
    c.due_at_utc,
    CASE c.value_format_id
        WHEN 0 /* JobPayloadFormat.None */ THEN 'none'
        WHEN 1 /* JobPayloadFormat.Json */ THEN 'json'
        WHEN 2 /* JobPayloadFormat.Bytes */ THEN 'bytes'
        WHEN 3 /* JobPayloadFormat.Text */ THEN 'text'
        ELSE CONCAT('custom-', c.value_format_id)
    END AS value_format,
    CASE
        WHEN
            c.value_format_id IN (1 /* JobPayloadFormat.Json */, 3 /* JobPayloadFormat.Text */)
            THEN CAST(c.value AS VARCHAR(MAX)) COLLATE latin1_general_100_bin2_utf8
    END AS value_text,
    c.created_at_utc,
    c.modified_at_utc,
    c.version
FROM {{schema}}.checkpoints AS c
JOIN {{schema}}.jobs AS j ON j.id = c.job_id
JOIN {{schema}}.namespaces AS ns ON ns.id = j.namespace_id
JOIN {{schema}}.definitions AS d ON d.id = j.definition_id
