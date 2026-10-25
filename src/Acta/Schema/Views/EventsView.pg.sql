SELECT
    e.event_id,
    e.created_at_utc,
    ns.namespace_name AS namespace,
    e.namespace_id,
    {{decode:entity:e.entity_code}} AS entity_kind,
    e.entity_code,
    e.entity_id,
    {{decode:event:e.event_code}} AS event,
    e.event_code,
    e.definition_id,
    d.definition_name AS job_name,
    e.tenant_id,
    t.tenant_name,
    e.lineage_root_job_id,
    root.job_ref AS lineage_root_job_ref,
    e.worker_id,
    wkr.worker_ref,
    e.execution_number,
    e.duration_ms,
    e.event_message,
    CASE e.details_format_id
        WHEN 0 /* JobPayloadFormat.None */ THEN 'none'
        WHEN 1 /* JobPayloadFormat.Json */ THEN 'json'
        WHEN 2 /* JobPayloadFormat.Bytes */ THEN 'bytes'
        ELSE 'custom-' || e.details_format_id::TEXT
    END AS details_format,
    CASE WHEN e.details_format_id = 1 /* JobPayloadFormat.Json */ THEN CONVERT_FROM(e.details, 'UTF8') END AS details_text
FROM {{schema}}.events AS e
JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = e.namespace_id
LEFT JOIN {{schema}}.definitions AS d ON d.definition_id = e.definition_id
LEFT JOIN {{schema}}.tenants AS t
    ON t.tenant_id = COALESCE(e.tenant_id, CASE WHEN e.entity_code = 120 /* EntityCode.Tenant */ THEN e.entity_id END)
LEFT JOIN {{schema}}.jobs AS root ON root.job_id = e.lineage_root_job_id
LEFT JOIN {{schema}}.workers AS wkr ON wkr.worker_id = e.worker_id
