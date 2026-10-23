SELECT
    j.job_id,
    ns.namespace_name AS job_namespace,
    jd.definition_name AS job_name,
    j.parent_job_id,
    j.lineage_root_job_id,
    j.deduplication_key,
    j.correlation_key,
    r.job_status_code AS status,
    r.job_priority_code AS priority,
    j.created_at_utc,
    r.modified_at_utc,
    r.next_run_at_utc,
    r.execution_number,
    r.failure_count,
    j.job_ref,
    pjob.job_ref AS parent_job_ref,
    rjob.job_ref AS lineage_root_job_ref,
    j.tenant_id,
    t.tenant_name,
    lane.lane_name AS lane
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
JOIN {{schema}}.namespaces ns ON ns.namespace_id = j.namespace_id
JOIN {{schema}}.definitions jd ON jd.definition_id = j.definition_id
LEFT JOIN {{schema}}.jobs pjob ON pjob.job_id = j.parent_job_id
LEFT JOIN {{schema}}.jobs rjob ON rjob.job_id = j.lineage_root_job_id
LEFT JOIN {{schema}}.tenants t ON t.tenant_id = j.tenant_id
LEFT JOIN {{schema}}.lanes lane ON lane.lane_id = r.lane_id
WHERE
    (
        @p_namespace_name IS NULL
        OR j.namespace_id = (SELECT n.namespace_id FROM {{schema}}.namespaces n WHERE n.namespace_name = @p_namespace_name)
    )
    AND (@p_job_status_code IS NULL OR r.job_status_code = @p_job_status_code)
    AND (@p_job_name IS NULL OR jd.definition_name = @p_job_name)
    AND (@p_parent_job_id IS NULL OR j.parent_job_id = @p_parent_job_id)
    AND (@p_tenant_id IS NULL OR j.tenant_id = @p_tenant_id)
    AND (@p_tenant_name IS NULL OR t.tenant_name = @p_tenant_name)
    AND (@p_correlation_key IS NULL OR j.correlation_key = @p_correlation_key)
    AND (@p_tag_filters IS NULL OR NOT EXISTS (
        SELECT 1
        FROM JSON_EACH(@p_tag_filters) f
        WHERE NOT EXISTS (
            SELECT 1
            FROM {{schema}}.tags t
            WHERE
                t.entity_code = 50 /* EntityCode.Job */ AND t.entity_id = j.job_id
                AND t.tag_name = JSON_EXTRACT(f.value, '$.tag_name')
                AND (
                    JSON_EXTRACT(f.value, '$.tag_value_search') IS NULL
                    OR t.tag_value_search = JSON_EXTRACT(f.value, '$.tag_value_search')
                )
        )
    ))
    AND (
        @p_terminal_only IS NULL
        OR r.job_status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
    )
    AND (@p_lane_filter IS NULL OR r.lane_id IN (SELECT l.lane_id FROM {{schema}}.lanes l WHERE l.lane_name = @p_lane_filter))
    AND (@p_recurring_only IS NULL OR EXISTS (
        SELECT 1
        FROM {{schema}}.schedules s
        WHERE s.job_id = j.job_id AND s.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
    ))
    AND (
        @p_cursor_created_at_utc IS NULL
        OR j.created_at_utc < @p_cursor_created_at_utc
        OR (j.created_at_utc = @p_cursor_created_at_utc AND j.job_id < @p_cursor_id)
    )
ORDER BY j.created_at_utc DESC, j.job_id DESC
LIMIT @p_take;

SELECT CASE WHEN @p_include_total IS NOT NULL THEN (
    SELECT COUNT(*)
    FROM {{schema}}.jobs j
    JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
    JOIN {{schema}}.definitions jd ON jd.definition_id = j.definition_id
    WHERE
        (
            @p_namespace_name IS NULL
            OR j.namespace_id = (SELECT n.namespace_id FROM {{schema}}.namespaces n WHERE n.namespace_name = @p_namespace_name)
        )
        AND (@p_job_status_code IS NULL OR r.job_status_code = @p_job_status_code)
        AND (@p_job_name IS NULL OR jd.definition_name = @p_job_name)
        AND (@p_parent_job_id IS NULL OR j.parent_job_id = @p_parent_job_id)
        AND (@p_tenant_id IS NULL OR j.tenant_id = @p_tenant_id)
        AND (
            @p_tenant_name IS NULL
            OR j.tenant_id IN (SELECT tk.tenant_id FROM {{schema}}.tenants tk WHERE tk.tenant_name = @p_tenant_name)
        )
        AND (@p_correlation_key IS NULL OR j.correlation_key = @p_correlation_key)
        AND (@p_tag_filters IS NULL OR NOT EXISTS (
            SELECT 1
            FROM JSON_EACH(@p_tag_filters) f
            WHERE NOT EXISTS (
                SELECT 1
                FROM {{schema}}.tags t
                WHERE
                    t.entity_code = 50 /* EntityCode.Job */ AND t.entity_id = j.job_id
                    AND t.tag_name = JSON_EXTRACT(f.value, '$.tag_name')
                    AND (
                        JSON_EXTRACT(f.value, '$.tag_value_search') IS NULL
                        OR t.tag_value_search = JSON_EXTRACT(f.value, '$.tag_value_search')
                    )
            )
        ))
        AND (
            @p_terminal_only IS NULL
            OR r.job_status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
        )
        AND (@p_lane_filter IS NULL OR r.lane_id IN (SELECT l.lane_id FROM {{schema}}.lanes l WHERE l.lane_name = @p_lane_filter))
        AND (@p_recurring_only IS NULL OR EXISTS (
            SELECT 1
            FROM {{schema}}.schedules s
            WHERE s.job_id = j.job_id AND s.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
        ))
) END;
