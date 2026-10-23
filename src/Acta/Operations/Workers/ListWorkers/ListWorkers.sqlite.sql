SELECT
    w.worker_id,
    ns.namespace_name AS job_namespace,
    w.worker_status_code AS status,
    w.host_name,
    w.deployment_version,
    w.engine_version,
    w.dotnet_version,
    w.process_id,
    w.max_concurrent_executors,
    w.last_seen_at_utc,
    w.created_at_utc AS started_at_utc,
    w.modified_at_utc,
    w.worker_ref
FROM {{schema}}.workers w
JOIN {{schema}}.namespaces ns ON ns.namespace_id = w.namespace_id
WHERE
    (@p_namespace_name IS NULL OR w.namespace_id = (SELECT n.namespace_id FROM {{schema}}.namespaces n WHERE n.namespace_name = @p_namespace_name))
    AND (@p_worker_status_code IS NULL OR w.worker_status_code = @p_worker_status_code)
    AND (
        @p_cursor_last_seen_at_utc IS NULL
        OR w.last_seen_at_utc < @p_cursor_last_seen_at_utc
        OR (w.last_seen_at_utc = @p_cursor_last_seen_at_utc AND w.worker_id < @p_cursor_int_id)
    )
ORDER BY w.last_seen_at_utc DESC, w.worker_id DESC
LIMIT @p_take;

SELECT CASE WHEN @p_include_total IS NOT NULL THEN (
    SELECT COUNT(*)
    FROM {{schema}}.workers w
    WHERE
        (@p_namespace_name IS NULL OR w.namespace_id = (SELECT n.namespace_id FROM {{schema}}.namespaces n WHERE n.namespace_name = @p_namespace_name))
        AND (@p_worker_status_code IS NULL OR w.worker_status_code = @p_worker_status_code)
) END;
