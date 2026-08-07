SELECT
    a.id,
    ns.name,
    a.job_id,
    a.origin_code,
    a.severity_code,
    a.kind_code,
    a.title,
    a.message,
    a.channel_name,
    a.occurrence_count,
    a.resolved_at_utc,
    a.delivery_status_code,
    a.retry_count,
    a.retry_after_utc,
    a.created_at_utc,
    a.modified_at_utc,
    a.job_ref,
    a.acknowledged_at_utc
FROM {{schema}}.alerts a
JOIN {{schema}}.namespaces ns ON ns.id = a.namespace_id
WHERE
    (@p_namespace_name IS NULL OR ns.name = @p_namespace_name)
    AND (@p_job_id IS NULL OR a.job_id = @p_job_id)
    AND (@p_unresolved_only IS NULL OR a.resolved_at_utc IS NULL)
    AND (@p_severity_code IS NULL OR a.severity_code >= @p_severity_code)
    AND (@p_delivery_status_code IS NULL OR a.delivery_status_code = @p_delivery_status_code)
    AND (@p_acknowledged IS NULL OR (a.acknowledged_at_utc IS NOT NULL) = @p_acknowledged)
    AND (@p_tag_filters IS NULL OR NOT EXISTS (
        SELECT 1 FROM JSON_EACH(@p_tag_filters) f
        WHERE NOT EXISTS (
            SELECT 1 FROM {{schema}}.tags tg
            WHERE
                tg.scope_code = 80 /* TagScopeCode.Alert */ AND tg.scope_id = a.id
                AND tg.name = JSON_EXTRACT(f.value, '$.name')
                AND (JSON_EXTRACT(f.value, '$.value_search') IS NULL OR tg.value_search = JSON_EXTRACT(f.value, '$.value_search'))
        )
    ))
    AND (
        @p_cursor_created_at_utc IS NULL
        OR a.created_at_utc < @p_cursor_created_at_utc
        OR (a.created_at_utc = @p_cursor_created_at_utc AND a.id < @p_cursor_id)
    )
ORDER BY a.created_at_utc DESC, a.id DESC
LIMIT @p_take;

SELECT CASE WHEN @p_include_total IS NOT NULL THEN (
    SELECT COUNT(*)
    FROM {{schema}}.alerts a
    JOIN {{schema}}.namespaces ns ON ns.id = a.namespace_id
    WHERE
        (@p_namespace_name IS NULL OR ns.name = @p_namespace_name)
        AND (@p_job_id IS NULL OR a.job_id = @p_job_id)
        AND (@p_unresolved_only IS NULL OR a.resolved_at_utc IS NULL)
        AND (@p_severity_code IS NULL OR a.severity_code >= @p_severity_code)
        AND (@p_delivery_status_code IS NULL OR a.delivery_status_code = @p_delivery_status_code)
        AND (@p_acknowledged IS NULL OR (a.acknowledged_at_utc IS NOT NULL) = @p_acknowledged)
        AND (@p_tag_filters IS NULL OR NOT EXISTS (
            SELECT 1 FROM JSON_EACH(@p_tag_filters) f
            WHERE NOT EXISTS (
                SELECT 1 FROM {{schema}}.tags tg
                WHERE
                    tg.scope_code = 80 /* TagScopeCode.Alert */ AND tg.scope_id = a.id
                    AND tg.name = JSON_EXTRACT(f.value, '$.name')
                    AND (JSON_EXTRACT(f.value, '$.value_search') IS NULL OR tg.value_search = JSON_EXTRACT(f.value, '$.value_search'))
            )
        ))
) END;
