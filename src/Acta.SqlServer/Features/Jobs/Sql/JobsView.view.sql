SELECT
    j.id AS job_id,
    j.job_ref,
    ns.name AS namespace,
    d.name AS job_name,
    {{decode:job-status:r.status_code}} AS status,
    r.status_code,
    j.deduplication_key,
    j.correlation_key,
    j.exclusive_key,
    j.parent_id,
    COALESCE(j.lineage_root_id, j.id) AS lineage_root_job_id,
    root.job_ref AS lineage_root_job_ref,
    j.tenant_id,
    {{decode:priority:r.priority_code}} AS priority,
    r.priority_code,
    CASE j.input_format_id
        WHEN 0 /* JobPayloadFormat.None */ THEN 'none'
        WHEN 1 /* JobPayloadFormat.Json */ THEN 'json'
        WHEN 2 /* JobPayloadFormat.Bytes */ THEN 'bytes'
        WHEN 3 /* JobPayloadFormat.Text */ THEN 'text'
        ELSE CONCAT('custom-', j.input_format_id)
    END AS input_format,
    CASE WHEN j.input_format_id IN (1 /* JobPayloadFormat.Json */, 3 /* JobPayloadFormat.Text */) THEN CAST(j.input AS varchar(max)) COLLATE Latin1_General_100_BIN2_UTF8 END AS input_text,
    lr.execution_number AS last_result_execution_number,
    CASE lr.result_format_id
        WHEN 1 /* JobPayloadFormat.Json */ THEN 'json'
        WHEN 2 /* JobPayloadFormat.Bytes */ THEN 'bytes'
        WHEN 3 /* JobPayloadFormat.Text */ THEN 'text'
        ELSE CASE WHEN lr.result_format_id IS NULL THEN NULL ELSE CONCAT('custom-', lr.result_format_id) END
    END AS last_result_format,
    CASE WHEN lr.result_format_id IN (1 /* JobPayloadFormat.Json */, 3 /* JobPayloadFormat.Text */) THEN CAST(lr.result AS varchar(max)) COLLATE Latin1_General_100_BIN2_UTF8 END AS last_result_text,
    lr.created_at_utc AS last_result_created_at_utc,
    r.next_run_at_utc,
    r.execution_number,
    r.failure_count,
    r.leased_by_worker_id,
    w.host AS leased_by_worker_host,
    r.lease_expires_at_utc,
    r.retention_until_utc,
    j.created_at_utc,
    r.modified_at_utc,
    r.version
FROM {{schema}}.jobs AS j
JOIN {{schema}}.runtimes AS r ON r.job_id = j.id
JOIN {{schema}}.namespaces AS ns ON ns.id = j.namespace_id
JOIN {{schema}}.definitions AS d ON d.id = j.definition_id
LEFT JOIN {{schema}}.jobs AS root ON root.id = COALESCE(j.lineage_root_id, j.id)
LEFT JOIN {{schema}}.workers AS w ON w.id = r.leased_by_worker_id
LEFT JOIN {{schema}}.results AS lr
  ON lr.job_id = j.id
 AND lr.execution_number = (SELECT MAX(rr.execution_number) FROM {{schema}}.results AS rr WHERE rr.job_id = j.id)
