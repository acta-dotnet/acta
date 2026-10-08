SELECT TOP (@p_take) ns.name
FROM {{schema}}.namespaces ns
WHERE
    (@p_name_search IS NULL OR ns.name LIKE @p_name_search)
    AND (@p_tag_filters IS NULL OR NOT EXISTS (
        SELECT 1
        FROM
            OPENJSON (@p_tag_filters)
            WITH (name VARCHAR(128) '$.name', value_search NVARCHAR(128) '$.value_search') f
        WHERE NOT EXISTS (
            SELECT 1 FROM {{schema}}.tags tg
            WHERE
                tg.scope_code = 30 /* TagScopeCode.Namespace */ AND tg.scope_id = ns.id
                AND tg.name = f.name
                AND (f.value_search IS NULL OR tg.value_search = f.value_search)
        )
    ))
    AND (@p_cursor_namespace_name IS NULL OR ns.name > @p_cursor_namespace_name)
ORDER BY ns.name ASC;

SELECT CASE WHEN @p_include_total IS NOT NULL THEN (
    SELECT COUNT(*)
    FROM {{schema}}.namespaces ns
    WHERE
        (@p_name_search IS NULL OR ns.name LIKE @p_name_search)
        AND (@p_tag_filters IS NULL OR NOT EXISTS (
            SELECT 1
            FROM
                OPENJSON (@p_tag_filters)
                WITH (name VARCHAR(128) '$.name', value_search NVARCHAR(128) '$.value_search') f
            WHERE NOT EXISTS (
                SELECT 1 FROM {{schema}}.tags tg
                WHERE
                    tg.scope_code = 30 /* TagScopeCode.Namespace */ AND tg.scope_id = ns.id
                    AND tg.name = f.name
                    AND (f.value_search IS NULL OR tg.value_search = f.value_search)
            )
        ))
) END;
