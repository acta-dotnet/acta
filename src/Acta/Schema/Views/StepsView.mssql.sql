SELECT
    s.job_id,
    j.job_ref,
    ns.namespace_name AS namespace,
    d.definition_name AS job_name,
    s.step_name,
    {{decode:job-step-status:s.step_status_code}} AS status,
    s.step_status_code,
    s.attempt_number,
    s.next_retry_at_utc,
    {{decode:step-failure:s.failure_code}} AS failure,
    s.failure_code,
    s.failure_message,
    CASE s.result_format_id
        WHEN 0 /* JobPayloadFormat.None */ THEN 'none'
        WHEN 1 /* JobPayloadFormat.Json */ THEN 'json'
        WHEN 2 /* JobPayloadFormat.Bytes */ THEN 'bytes'
        ELSE CONCAT('custom-', s.result_format_id)
    END AS result_format,
    CASE
        WHEN
            s.result_format_id = 1 /* JobPayloadFormat.Json */
            THEN CAST(s.result AS VARCHAR(MAX)) COLLATE latin1_general_100_bin2_utf8
    END AS result_text,
    s.created_at_utc,
    s.modified_at_utc,
    s.version
FROM {{schema}}.steps AS s
JOIN {{schema}}.jobs AS j ON j.job_id = s.job_id
JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = j.namespace_id
JOIN {{schema}}.definitions AS d ON d.definition_id = j.definition_id
