SELECT TOP (@p_take)
    s.id,
    s.job_id,
    s.definition_id,
    ns.name,
    jd.name,
    s.name,
    s.origin_code,
    s.expression_kind_code,
    s.expression_effective,
    s.time_zone_id_effective,
    s.misfire_strategy_code,
    s.next_run_at_utc,
    s.last_occurrence_at_utc,
    s.status_code,
    s.paused_until_utc,
    s.created_at_utc,
    s.modified_at_utc,
    s.version
FROM {{schema}}.schedules s
JOIN {{schema}}.namespaces ns ON ns.id = s.namespace_id
JOIN {{schema}}.definitions jd ON jd.id = s.definition_id
WHERE
    s.next_run_at_utc IS NOT NULL
    AND (@p_live_only IS NULL OR s.status_code <> 230 /* ScheduleStatusCode.Orphaned */)
    AND (@p_namespace_name IS NULL OR ns.name = @p_namespace_name)
    AND (@p_job_name IS NULL OR jd.name = @p_job_name)
    AND (@p_origin_code IS NULL OR s.origin_code = @p_origin_code)
    AND (@p_tag_filters IS NULL OR NOT EXISTS (
        SELECT 1
        FROM
            OPENJSON (@p_tag_filters)
            WITH (name VARCHAR(128) '$.name', value_search NVARCHAR(128) '$.value_search') f
        WHERE NOT EXISTS (
            SELECT 1 FROM {{schema}}.tags tg
            WHERE
                tg.scope_code = 60 /* TagScopeCode.Schedule */ AND tg.scope_id = s.id
                AND tg.name = f.name
                AND (f.value_search IS NULL OR tg.value_search = f.value_search)
        )
    ))
    AND (
        @p_cursor_next_run_at_utc IS NULL
        OR s.next_run_at_utc > @p_cursor_next_run_at_utc
        OR (s.next_run_at_utc = @p_cursor_next_run_at_utc AND s.id > @p_cursor_id)
    )
ORDER BY s.next_run_at_utc ASC, s.id ASC
OPTION (RECOMPILE);

SELECT
    CASE WHEN @p_include_total IS NOT NULL THEN (
        SELECT COUNT(*)
        FROM {{schema}}.schedules s
        JOIN {{schema}}.namespaces ns ON ns.id = s.namespace_id
        JOIN {{schema}}.definitions jd ON jd.id = s.definition_id
        WHERE
            s.next_run_at_utc IS NOT NULL
            AND (@p_live_only IS NULL OR s.status_code <> 230 /* ScheduleStatusCode.Orphaned */)
            AND (@p_namespace_name IS NULL OR ns.name = @p_namespace_name)
            AND (@p_job_name IS NULL OR jd.name = @p_job_name)
            AND (@p_origin_code IS NULL OR s.origin_code = @p_origin_code)
            AND (@p_tag_filters IS NULL OR NOT EXISTS (
                SELECT 1
                FROM
                    OPENJSON (@p_tag_filters)
                    WITH (name VARCHAR(128) '$.name', value_search NVARCHAR(128) '$.value_search') f
                WHERE NOT EXISTS (
                    SELECT 1 FROM {{schema}}.tags tg
                    WHERE
                        tg.scope_code = 60 /* TagScopeCode.Schedule */ AND tg.scope_id = s.id
                        AND tg.name = f.name
                        AND (f.value_search IS NULL OR tg.value_search = f.value_search)
                )
            ))
    ) END
OPTION (RECOMPILE);
