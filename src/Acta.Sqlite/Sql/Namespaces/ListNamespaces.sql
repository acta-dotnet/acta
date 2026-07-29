SELECT ns.name
  FROM {{schema}}.namespaces ns
 WHERE (@p_name_prefix IS NULL OR ns.name LIKE @p_name_prefix)
AND (@p_tag_filters IS NULL OR NOT EXISTS (
        SELECT 1 FROM json_each(@p_tag_filters) f
         WHERE NOT EXISTS (
               SELECT 1 FROM {{schema}}.tags tg
                WHERE tg.scope_code = 30 /* TagScopeCode.Namespace */ AND tg.scope_id = ns.id
                  AND tg.name = json_extract(f.value, '$.name')
                  AND (json_extract(f.value, '$.value_search') IS NULL OR tg.value_search = json_extract(f.value, '$.value_search'))
         )
        ))
      AND (@p_cursor_namespace_name IS NULL OR ns.name > @p_cursor_namespace_name)
 ORDER BY ns.name ASC
 LIMIT @p_take;

SELECT CASE WHEN @p_include_total IS NOT NULL THEN (
         SELECT COUNT(*)
           FROM {{schema}}.namespaces ns
          WHERE (@p_name_prefix IS NULL OR ns.name LIKE @p_name_prefix)
            AND (@p_tag_filters IS NULL OR NOT EXISTS (
                 SELECT 1 FROM json_each(@p_tag_filters) f
                  WHERE NOT EXISTS (
                        SELECT 1 FROM {{schema}}.tags tg
                         WHERE tg.scope_code = 30 /* TagScopeCode.Namespace */ AND tg.scope_id = ns.id
                           AND tg.name = json_extract(f.value, '$.name')
                           AND (json_extract(f.value, '$.value_search') IS NULL OR tg.value_search = json_extract(f.value, '$.value_search'))
                  )
                 ))
       ) END;
