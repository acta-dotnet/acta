SELECT
    w.id AS worker_id,
    ns.name AS job_namespace,
    w.status_code AS status,
    w.host,
    w.deployment_version,
    w.engine_version,
    w.dotnet_version,
    w.process_id,
    w.max_concurrency,
    w.last_seen_at_utc AS last_heartbeat_at_utc,
    w.created_at_utc AS started_at_utc,
    w.modified_at_utc,
    w.worker_ref
FROM {{schema}}.workers w
JOIN {{schema}}.namespaces ns ON ns.id = w.namespace_id
WHERE
    (@p_namespace_name IS NULL OR ns.name = @p_namespace_name)
    AND (@p_status_code IS NULL OR w.status_code = @p_status_code)
    AND (
        @p_cursor_last_seen_at_utc IS NULL
        OR w.last_seen_at_utc < @p_cursor_last_seen_at_utc
        OR (w.last_seen_at_utc = @p_cursor_last_seen_at_utc AND w.id < @p_cursor_int_id)
    )
ORDER BY w.last_seen_at_utc DESC, w.id DESC
LIMIT @p_take;

SELECT CASE WHEN @p_include_total IS NOT NULL THEN (
    SELECT COUNT(*)
    FROM {{schema}}.workers w
    JOIN {{schema}}.namespaces ns ON ns.id = w.namespace_id
    WHERE
        (@p_namespace_name IS NULL OR ns.name = @p_namespace_name)
        AND (@p_status_code IS NULL OR w.status_code = @p_status_code)
) END;
