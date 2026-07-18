SELECT
    s.id AS step_id,
    s.job_id,
    j.job_ref,
    ns.name AS namespace,
    d.name AS job_name,
    s.name AS step_name,
    {{decode:job-step-state:s.state_code}} AS state,
    s.state_code,
    s.attempt_number,
    s.next_retry_at_utc,
    {{decode:job-event-reason:s.reason_code}} AS reason,
    s.reason_code,
    s.reason_message,
    CASE s.result_format_id
        WHEN 0 /* JobPayloadFormat.None */ THEN 'none'
        WHEN 1 /* JobPayloadFormat.Json */ THEN 'json'
        WHEN 2 /* JobPayloadFormat.Bytes */ THEN 'bytes'
        WHEN 3 /* JobPayloadFormat.Text */ THEN 'text'
        ELSE CONCAT('custom-', s.result_format_id)
    END AS result_format,
    CASE WHEN s.result_format_id IN (1 /* JobPayloadFormat.Json */, 3 /* JobPayloadFormat.Text */) THEN CAST(s.result AS varchar(max)) COLLATE Latin1_General_100_BIN2_UTF8 END AS result_text,
    s.created_at_utc,
    s.modified_at_utc,
    s.version
FROM {{schema}}.steps AS s
JOIN {{schema}}.jobs AS j ON j.id = s.job_id
JOIN {{schema}}.namespaces AS ns ON ns.id = j.namespace_id
JOIN {{schema}}.definitions AS d ON d.id = j.definition_id
