SELECT ns.namespace_id, ns.namespace_name AS job_namespace, ns.namespace_status_code AS status, ns.owner_team, ns.description, ns.version
FROM {{schema}}.namespaces ns
WHERE
    (@p_name_search IS NULL OR ns.namespace_name LIKE @p_name_search)
    AND (@p_namespace_status_code IS NULL OR ns.namespace_status_code = @p_namespace_status_code)
    AND (@p_tag_filters IS NULL OR NOT EXISTS (
        SELECT 1
        FROM jsonb_array_elements(@p_tag_filters::jsonb) AS f (value)
        WHERE NOT EXISTS (
            SELECT 1
            FROM {{schema}}.tags tg
            WHERE
                tg.entity_code = 80 /* EntityCode.Namespace */ AND tg.entity_id = ns.namespace_id
                AND tg.tag_name = f.value ->> 'tag_name'
                AND ((f.value ->> 'tag_value_search') IS NULL OR tg.tag_value_search = f.value ->> 'tag_value_search')
        )
    ))
    AND (@p_cursor_namespace_name IS NULL OR ns.namespace_name > @p_cursor_namespace_name)
ORDER BY ns.namespace_name ASC
LIMIT @p_take;

SELECT CASE WHEN @p_include_total IS NOT NULL THEN (
    SELECT COUNT(*)
    FROM {{schema}}.namespaces ns
    WHERE
        (@p_name_search IS NULL OR ns.namespace_name LIKE @p_name_search)
        AND (@p_namespace_status_code IS NULL OR ns.namespace_status_code = @p_namespace_status_code)
        AND (@p_tag_filters IS NULL OR NOT EXISTS (
            SELECT 1
            FROM jsonb_array_elements(@p_tag_filters::jsonb) AS f (value)
            WHERE NOT EXISTS (
                SELECT 1
                FROM {{schema}}.tags tg
                WHERE
                    tg.entity_code = 80 /* EntityCode.Namespace */ AND tg.entity_id = ns.namespace_id
                    AND tg.tag_name = f.value ->> 'tag_name'
                    AND ((f.value ->> 'tag_value_search') IS NULL OR tg.tag_value_search = f.value ->> 'tag_value_search')
            )
        ))
) END;
