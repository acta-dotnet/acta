SELECT
    e.id AS event_id,
    e.created_at_utc,
    ns.name AS namespace,
    e.job_id,
    e.job_ref,
    e.lineage_root_id AS lineage_root_job_id,
    root.job_ref AS lineage_root_job_ref,
    e.definition_id,
    d.name AS job_name,
    {{decode:event:e.event_code}} AS event,
    e.event_code,
    {{decode:actor:e.actor_code}} AS actor,
    e.actor_code,
    e.actor_key,
    {{decode:job-status:e.from_status_code}} AS from_status,
    e.from_status_code,
    {{decode:job-status:e.to_status_code}} AS to_status,
    e.to_status_code,
    {{decode:execution-status:e.execution_status_code}} AS execution_status,
    e.execution_status_code,
    {{decode:job-event-reason:e.reason_code}} AS reason,
    e.reason_code,
    e.reason_message,
    CASE e.detail_format_id
        WHEN 0 /* JobPayloadFormat.None */ THEN 'none'
        WHEN 1 /* JobPayloadFormat.Json */ THEN 'json'
        WHEN 2 /* JobPayloadFormat.Bytes */ THEN 'bytes'
        WHEN 3 /* JobPayloadFormat.Text */ THEN 'text'
        ELSE 'custom-' || CAST(e.detail_format_id AS TEXT)
    END AS detail_format,
    CASE WHEN e.detail_format_id IN (1 /* JobPayloadFormat.Json */, 3 /* JobPayloadFormat.Text */) THEN CAST(e.detail AS TEXT) END
        AS detail_text,
    e.worker_id,
    wkr.worker_ref,
    e.execution_number,
    e.duration_ms,
    e.tenant_id
FROM {{schema}}.events AS e
JOIN {{schema}}.namespaces AS ns ON ns.id = e.namespace_id
LEFT JOIN {{schema}}.definitions AS d ON d.id = e.definition_id
LEFT JOIN {{schema}}.jobs AS root ON root.id = e.lineage_root_id
LEFT JOIN {{schema}}.workers AS wkr ON wkr.id = e.worker_id
