SELECT TOP (@p_take)
    a.alert_id,
    ns.namespace_name AS job_namespace,
    a.job_id,
    a.alert_origin_code AS origin,
    a.alert_severity_code AS severity,
    a.alert_kind_code AS kind,
    a.title,
    a.alert_message AS message,
    a.channel_name,
    a.occurrence_count,
    a.resolved_at_utc,
    a.delivery_status_code AS delivery_status,
    a.retry_count,
    a.retry_after_utc,
    a.created_at_utc,
    a.modified_at_utc,
    a.job_ref,
    a.acknowledged_at_utc,
    a.alert_ref
FROM {{schema}}.alerts a
JOIN {{schema}}.namespaces ns ON ns.namespace_id = a.namespace_id
WHERE
    (@p_namespace_name IS NULL OR ns.namespace_name = @p_namespace_name)
    AND (@p_job_id IS NULL OR a.job_id = @p_job_id)
    AND (@p_unresolved_only IS NULL OR a.resolved_at_utc IS NULL)
    AND (@p_alert_severity_code IS NULL OR a.alert_severity_code >= @p_alert_severity_code)
    AND (@p_delivery_status_code IS NULL OR a.delivery_status_code = @p_delivery_status_code)
    AND (@p_acknowledged IS NULL OR (CASE WHEN a.acknowledged_at_utc IS NOT NULL THEN 1 ELSE 0 END) = @p_acknowledged)
    AND (
        @p_cursor_created_at_utc IS NULL
        OR a.created_at_utc < @p_cursor_created_at_utc
        OR (a.created_at_utc = @p_cursor_created_at_utc AND a.alert_id < @p_cursor_id)
    )
ORDER BY a.created_at_utc DESC, a.alert_id DESC
OPTION (RECOMPILE);

SELECT
    CASE WHEN @p_include_total IS NOT NULL THEN (
        SELECT COUNT(*)
        FROM {{schema}}.alerts a
        JOIN {{schema}}.namespaces ns ON ns.namespace_id = a.namespace_id
        WHERE
            (@p_namespace_name IS NULL OR ns.namespace_name = @p_namespace_name)
            AND (@p_job_id IS NULL OR a.job_id = @p_job_id)
            AND (@p_unresolved_only IS NULL OR a.resolved_at_utc IS NULL)
            AND (@p_alert_severity_code IS NULL OR a.alert_severity_code >= @p_alert_severity_code)
            AND (@p_delivery_status_code IS NULL OR a.delivery_status_code = @p_delivery_status_code)
            AND (@p_acknowledged IS NULL OR (CASE WHEN a.acknowledged_at_utc IS NOT NULL THEN 1 ELSE 0 END) = @p_acknowledged)
    ) END
OPTION (RECOMPILE);
