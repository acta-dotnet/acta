SELECT
    j.job_id,
    j.job_ref,
    ns.namespace_name AS namespace,
    d.definition_name AS job_name,
    {{decode:job-status:r.job_status_code}} AS status,
    r.job_status_code,
    j.deduplication_key,
    j.correlation_key,
    j.concurrency_key,
    j.parent_job_id,
    COALESCE(j.lineage_root_job_id, j.job_id) AS lineage_root_job_id,
    root.job_ref AS lineage_root_job_ref,
    j.tenant_id,
    t.tenant_name,
    {{decode:job-priority:r.job_priority_code}} AS priority,
    r.job_priority_code,
    CASE j.input_format_id
        WHEN 0 /* JobPayloadFormat.None */ THEN 'none'
        WHEN 1 /* JobPayloadFormat.Json */ THEN 'json'
        WHEN 2 /* JobPayloadFormat.Bytes */ THEN 'bytes'
        WHEN 3 /* JobPayloadFormat.Text */ THEN 'text'
        ELSE 'custom-' || CAST(j.input_format_id AS TEXT)
    END AS input_format,
    CASE WHEN j.input_format_id IN (1 /* JobPayloadFormat.Json */, 3 /* JobPayloadFormat.Text */) THEN CAST(j.input AS TEXT) END
        AS input_text,
    CASE r.result_format_id
        WHEN 0 /* JobPayloadFormat.None */ THEN 'none'
        WHEN 1 /* JobPayloadFormat.Json */ THEN 'json'
        WHEN 2 /* JobPayloadFormat.Bytes */ THEN 'bytes'
        WHEN 3 /* JobPayloadFormat.Text */ THEN 'text'
        ELSE 'custom-' || CAST(r.result_format_id AS TEXT)
    END AS result_format,
    CASE WHEN r.result_format_id IN (1 /* JobPayloadFormat.Json */, 3 /* JobPayloadFormat.Text */) THEN CAST(r.result AS TEXT) END
        AS result_text,
    r.next_run_at_utc,
    r.execution_number,
    r.failure_count,
    r.leased_by_worker_id,
    w.host_name AS leased_by_worker_host_name,
    r.lease_expires_at_utc,
    r.retention_until_utc,
    j.created_at_utc,
    r.modified_at_utc,
    r.version,
    lane.lane_name AS lane
FROM {{schema}}.jobs AS j
JOIN {{schema}}.jobs_state AS r ON r.job_id = j.job_id
JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = j.namespace_id
JOIN {{schema}}.definitions AS d ON d.definition_id = j.definition_id
LEFT JOIN {{schema}}.jobs AS root ON root.job_id = COALESCE(j.lineage_root_job_id, j.job_id)
LEFT JOIN {{schema}}.tenants AS t ON t.tenant_id = j.tenant_id
LEFT JOIN {{schema}}.workers AS w ON w.worker_id = r.leased_by_worker_id
LEFT JOIN {{schema}}.lanes AS lane ON lane.lane_id = r.lane_id
