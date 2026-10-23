SELECT
    jd.definition_id,
    ns.namespace_name AS job_namespace,
    jd.definition_name AS job_name,
    jd.definition_status_code AS status,
    jd.input_type_name,
    jd.output_type_name,
    jd.job_priority_code_override AS priority_override,
    jd.job_priority_code_effective AS priority_effective,
    jd.max_attempts_override,
    jd.max_attempts_effective,
    jd.concurrency_limit_override,
    jd.concurrency_limit_effective,
    jd.rate_limit_override,
    jd.rate_limit_effective,
    jd.rate_name,
    jd.lane_name AS lane,
    jd.modified_at_utc,
    jd.version
FROM {{schema}}.definitions jd
JOIN {{schema}}.namespaces ns ON ns.namespace_id = jd.namespace_id
WHERE
    (
        @p_namespace_name IS NULL
        OR jd.namespace_id = (SELECT n.namespace_id FROM {{schema}}.namespaces n WHERE n.namespace_name = @p_namespace_name)
    )
    AND (@p_name_search IS NULL OR jd.definition_name LIKE @p_name_search)
    AND (@p_definition_status_code IS NULL OR jd.definition_status_code = @p_definition_status_code)
    AND (@p_tag_filters IS NULL OR NOT EXISTS (
        SELECT 1
        FROM jsonb_array_elements(@p_tag_filters::jsonb) AS f (value)
        WHERE NOT EXISTS (
            SELECT 1
            FROM {{schema}}.tags tg
            WHERE
                tg.entity_code = 30 /* EntityCode.Definition */ AND tg.entity_id = jd.definition_id
                AND tg.tag_name = f.value ->> 'tag_name'
                AND ((f.value ->> 'tag_value_search') IS NULL OR tg.tag_value_search = f.value ->> 'tag_value_search')
        )
    ))
    AND (@p_cursor_namespace_name IS NULL
        OR ns.namespace_name > @p_cursor_namespace_name
        OR (ns.namespace_name = @p_cursor_namespace_name AND jd.definition_name > @p_cursor_job_name)
        OR (
            ns.namespace_name = @p_cursor_namespace_name
            AND jd.definition_name = @p_cursor_job_name
            AND jd.definition_id > @p_cursor_int_id
        ))
ORDER BY ns.namespace_name ASC, jd.definition_name ASC, jd.definition_id ASC
LIMIT @p_take;

SELECT CASE WHEN @p_include_total IS NOT NULL THEN (
    SELECT COUNT(*)
    FROM {{schema}}.definitions jd
    WHERE
        (
            @p_namespace_name IS NULL
            OR jd.namespace_id = (SELECT n.namespace_id FROM {{schema}}.namespaces n WHERE n.namespace_name = @p_namespace_name)
        )
        AND (@p_name_search IS NULL OR jd.definition_name LIKE @p_name_search)
        AND (@p_definition_status_code IS NULL OR jd.definition_status_code = @p_definition_status_code)
        AND (@p_tag_filters IS NULL OR NOT EXISTS (
            SELECT 1
            FROM jsonb_array_elements(@p_tag_filters::jsonb) AS f (value)
            WHERE NOT EXISTS (
                SELECT 1
                FROM {{schema}}.tags tg
                WHERE
                    tg.entity_code = 30 /* EntityCode.Definition */ AND tg.entity_id = jd.definition_id
                    AND tg.tag_name = f.value ->> 'tag_name'
                    AND ((f.value ->> 'tag_value_search') IS NULL OR tg.tag_value_search = f.value ->> 'tag_value_search')
            )
        ))
) END;
