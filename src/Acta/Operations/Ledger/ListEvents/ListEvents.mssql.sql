-- A tenant event's subject is its tenant, so the tenant join and filters read the subject when the dimension is NULL.
SELECT TOP (@p_take)
    e.event_id,
    e.event_code,
    e.created_at_utc,
    ns.namespace_name AS job_namespace,
    e.entity_code,
    e.entity_id,
    jd.definition_name AS job_name,
    sj.job_ref,
    e.lineage_root_job_id,
    rjob.job_ref AS lineage_root_job_ref,
    e.definition_id,
    t.tenant_id,
    t.tenant_name,
    e.worker_id,
    wkr.worker_ref,
    e.execution_number,
    e.duration_ms,
    e.event_message,
    e.details_format_id,
    e.details
FROM {{schema}}.events e
JOIN {{schema}}.namespaces ns ON ns.namespace_id = e.namespace_id
LEFT JOIN {{schema}}.jobs sj ON e.entity_code = 50 /* EntityCode.Job */ AND sj.job_id = e.entity_id
LEFT JOIN {{schema}}.jobs rjob ON rjob.job_id = e.lineage_root_job_id
LEFT JOIN {{schema}}.definitions jd ON jd.definition_id = e.definition_id
LEFT JOIN {{schema}}.tenants t
    ON t.tenant_id = COALESCE(e.tenant_id, CASE WHEN e.entity_code = 120 /* EntityCode.Tenant */ THEN e.entity_id END)
LEFT JOIN {{schema}}.workers wkr ON wkr.worker_id = e.worker_id
WHERE
    (@p_job_id IS NULL OR (e.entity_code = 50 /* EntityCode.Job */ AND e.entity_id = @p_job_id))
    AND (@p_lineage_root_job_id IS NULL OR e.lineage_root_job_id = @p_lineage_root_job_id)
    AND (@p_namespace_name IS NULL OR ns.namespace_name = @p_namespace_name)
    AND (@p_event_code IS NULL OR e.event_code = @p_event_code)
    AND (@p_entity_code IS NULL OR e.entity_code = @p_entity_code)
    AND (@p_entity_id IS NULL OR e.entity_id = @p_entity_id)
    AND (@p_definition_id IS NULL OR e.definition_id = @p_definition_id)
    AND (@p_tenant_id IS NULL OR t.tenant_id = @p_tenant_id)
    AND (@p_tenant_name IS NULL OR t.tenant_name = @p_tenant_name)
    AND (@p_worker_id IS NULL OR e.worker_id = @p_worker_id)
    AND (@p_created_from_utc IS NULL OR e.created_at_utc >= @p_created_from_utc)
    AND (@p_created_to_utc IS NULL OR e.created_at_utc < @p_created_to_utc)
    AND (
        @p_cursor_created_at_utc IS NULL
        OR e.created_at_utc < @p_cursor_created_at_utc
        OR (e.created_at_utc = @p_cursor_created_at_utc AND e.event_id < @p_cursor_id)
    )
ORDER BY e.created_at_utc DESC, e.event_id DESC
OPTION (RECOMPILE);

SELECT
    CASE WHEN @p_include_total IS NOT NULL THEN (
        SELECT COUNT(*)
        FROM {{schema}}.events e
        JOIN {{schema}}.namespaces ns ON ns.namespace_id = e.namespace_id
        LEFT JOIN {{schema}}.tenants t
            ON t.tenant_id = COALESCE(e.tenant_id, CASE WHEN e.entity_code = 120 /* EntityCode.Tenant */ THEN e.entity_id END)
        WHERE
            (@p_job_id IS NULL OR (e.entity_code = 50 /* EntityCode.Job */ AND e.entity_id = @p_job_id))
            AND (@p_lineage_root_job_id IS NULL OR e.lineage_root_job_id = @p_lineage_root_job_id)
            AND (@p_namespace_name IS NULL OR ns.namespace_name = @p_namespace_name)
            AND (@p_event_code IS NULL OR e.event_code = @p_event_code)
            AND (@p_entity_code IS NULL OR e.entity_code = @p_entity_code)
            AND (@p_entity_id IS NULL OR e.entity_id = @p_entity_id)
            AND (@p_definition_id IS NULL OR e.definition_id = @p_definition_id)
            AND (@p_tenant_id IS NULL OR t.tenant_id = @p_tenant_id)
            AND (@p_tenant_name IS NULL OR t.tenant_name = @p_tenant_name)
            AND (@p_worker_id IS NULL OR e.worker_id = @p_worker_id)
            AND (@p_created_from_utc IS NULL OR e.created_at_utc >= @p_created_from_utc)
            AND (@p_created_to_utc IS NULL OR e.created_at_utc < @p_created_to_utc)
    ) END
OPTION (RECOMPILE);
