DECLARE @ns_id INT = NULL;

IF @p_namespace_name IS NOT NULL
    BEGIN
        SELECT @ns_id = id
        FROM {{schema}}.namespaces
        WHERE name = @p_namespace_name;
    END;

SELECT TOP (@p_take)
    j.id,
    ns.name,
    jd.name,
    j.parent_id,
    j.lineage_root_id,
    j.deduplication_key,
    j.correlation_key,
    r.status_code,
    r.priority_code,
    j.created_at_utc,
    r.modified_at_utc,
    r.next_run_at_utc,
    r.execution_number,
    r.failure_count,
    j.job_ref,
    pjob.job_ref AS parent_job_ref,
    rjob.job_ref AS lineage_root_job_ref,
    j.tenant_id,
    t.tenant_key
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
JOIN {{schema}}.namespaces ns ON ns.id = j.namespace_id
JOIN {{schema}}.definitions jd ON jd.id = j.definition_id
LEFT JOIN {{schema}}.jobs pjob ON pjob.id = j.parent_id
LEFT JOIN {{schema}}.jobs rjob ON rjob.id = j.lineage_root_id
LEFT JOIN {{schema}}.tenants t ON t.id = j.tenant_id
WHERE
    (@p_namespace_name IS NULL OR j.namespace_id = @ns_id)
    AND (@p_status_code IS NULL OR r.status_code = @p_status_code)
    AND (@p_job_name IS NULL OR jd.name = @p_job_name)
    AND (@p_parent_id IS NULL OR j.parent_id = @p_parent_id)
    AND (@p_tenant_id IS NULL OR j.tenant_id = @p_tenant_id)
    AND (@p_tenant_key IS NULL OR t.tenant_key = @p_tenant_key)
    AND (@p_correlation_key IS NULL OR j.correlation_key = @p_correlation_key)
    AND (@p_tag_filters IS NULL OR NOT EXISTS (
        SELECT 1
        FROM
            OPENJSON (@p_tag_filters)
            WITH (name VARCHAR(128) '$.name', value_search NVARCHAR(128) '$.value_search') f
        WHERE NOT EXISTS (
            SELECT 1
            FROM {{schema}}.tags t
            WHERE
                t.scope_code = 50 /* TagScopeCode.Job */ AND t.scope_id = j.id
                AND t.name = f.name
                AND (f.value_search IS NULL OR t.value_search = f.value_search)
        )
    ))
    AND (
        @p_terminal_only IS NULL
        OR r.status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
    )
    AND (@p_recurring_only IS NULL OR EXISTS (
        SELECT 1
        FROM {{schema}}.schedules s
        WHERE s.job_id = j.id AND s.status_code <> 230 /* ScheduleStatusCode.Orphaned */
    ))
    AND (
        @p_cursor_created_at_utc IS NULL
        OR j.created_at_utc < @p_cursor_created_at_utc
        OR (j.created_at_utc = @p_cursor_created_at_utc AND j.id < @p_cursor_id)
    )
ORDER BY j.created_at_utc DESC, j.id DESC
OPTION (RECOMPILE);

SELECT
    CASE WHEN @p_include_total IS NOT NULL THEN (
        SELECT COUNT(*)
        FROM {{schema}}.jobs j
        JOIN {{schema}}.runtimes r ON r.job_id = j.id
        WHERE
            (@p_namespace_name IS NULL OR j.namespace_id = @ns_id)
            AND (@p_status_code IS NULL OR r.status_code = @p_status_code)
            AND (@p_job_name IS NULL OR EXISTS (
                SELECT 1
                FROM {{schema}}.definitions jd
                WHERE
                    jd.id = j.definition_id
                    AND jd.name = @p_job_name
            ))
            AND (@p_parent_id IS NULL OR j.parent_id = @p_parent_id)
            AND (@p_tenant_id IS NULL OR j.tenant_id = @p_tenant_id)
            AND (@p_correlation_key IS NULL OR j.correlation_key = @p_correlation_key)
            AND (@p_tag_filters IS NULL OR NOT EXISTS (
                SELECT 1
                FROM
                    OPENJSON (@p_tag_filters)
                    WITH (name VARCHAR(128) '$.name', value_search NVARCHAR(128) '$.value_search') f
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM {{schema}}.tags t
                    WHERE
                        t.scope_code = 50 /* TagScopeCode.Job */ AND t.scope_id = j.id
                        AND t.name = f.name
                        AND (f.value_search IS NULL OR t.value_search = f.value_search)
                )
            ))
            AND (
                @p_terminal_only IS NULL
                OR r.status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
            )
            AND (@p_recurring_only IS NULL OR EXISTS (
                SELECT 1
                FROM {{schema}}.schedules s
                WHERE s.job_id = j.id AND s.status_code <> 230 /* ScheduleStatusCode.Orphaned */
            ))
    ) END
OPTION (RECOMPILE);
