SELECT jd.id, ns.name, jd.name, jd.status_code, jd.input_type_name, jd.output_type_name,
       jd.priority_code_override, jd.priority_code_effective,
       jd.max_attempts_override, jd.max_attempts_effective,
       jd.modified_at_utc
  FROM {{schema}}.definitions jd
  JOIN {{schema}}.namespaces ns ON ns.id = jd.namespace_id
 WHERE (@p_namespace_name IS NULL OR ns.name = @p_namespace_name)
   AND (@p_status_code IS NULL OR jd.status_code = @p_status_code)
AND (@p_tag_filters IS NULL OR NOT EXISTS (
        SELECT 1 FROM json_each(@p_tag_filters) f
         WHERE NOT EXISTS (
               SELECT 1 FROM {{schema}}.tags tg
                WHERE tg.scope_code = 40 /* TagScopeCode.Definition */ AND tg.scope_id = jd.id
                  AND tg.name = json_extract(f.value, '$.name')
                  AND (json_extract(f.value, '$.value_search') IS NULL OR tg.value_search = json_extract(f.value, '$.value_search'))
         )
        ))
      AND (@p_cursor_namespace_name IS NULL
        OR ns.name > @p_cursor_namespace_name
        OR (ns.name = @p_cursor_namespace_name AND jd.name > @p_cursor_job_name)
        OR (ns.name = @p_cursor_namespace_name AND jd.name = @p_cursor_job_name AND jd.id > @p_cursor_int_id))
 ORDER BY ns.name ASC, jd.name ASC, jd.id ASC
 LIMIT @p_take;

SELECT CASE WHEN @p_include_total IS NOT NULL THEN (
         SELECT COUNT(*)
           FROM {{schema}}.definitions jd
           JOIN {{schema}}.namespaces ns ON ns.id = jd.namespace_id
          WHERE (@p_namespace_name IS NULL OR ns.name = @p_namespace_name)
            AND (@p_status_code IS NULL OR jd.status_code = @p_status_code)
            AND (@p_tag_filters IS NULL OR NOT EXISTS (
                 SELECT 1 FROM json_each(@p_tag_filters) f
                  WHERE NOT EXISTS (
                        SELECT 1 FROM {{schema}}.tags tg
                         WHERE tg.scope_code = 40 /* TagScopeCode.Definition */ AND tg.scope_id = jd.id
                           AND tg.name = json_extract(f.value, '$.name')
                           AND (json_extract(f.value, '$.value_search') IS NULL OR tg.value_search = json_extract(f.value, '$.value_search'))
                  )
                 ))
       ) END;
