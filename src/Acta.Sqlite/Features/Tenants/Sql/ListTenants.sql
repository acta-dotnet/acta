SELECT t.id, t.tenant_key, t.display_name, t.description, t.status_code, t.created_at_utc, t.modified_at_utc, t.version
  FROM {{schema}}.tenants t
 WHERE (@p_search IS NULL OR instr(LOWER(t.tenant_key), @p_search) > 0 OR instr(LOWER(t.display_name), @p_search) > 0 OR instr(LOWER(t.description), @p_search) > 0)
   AND (@p_status_code IS NULL OR t.status_code = @p_status_code)
AND (@p_tag_filters IS NULL OR NOT EXISTS (
        SELECT 1 FROM json_each(@p_tag_filters) f
         WHERE NOT EXISTS (
               SELECT 1 FROM {{schema}}.tags tg
                WHERE tg.scope_code = 20 /* TagScopeCode.Tenant */ AND tg.scope_id = t.id
                  AND tg.name = json_extract(f.value, '$.name')
                  AND (json_extract(f.value, '$.value_search') IS NULL OR tg.value_search = json_extract(f.value, '$.value_search'))
         )
        ))
      AND (@p_cursor_tenant_key IS NULL OR t.tenant_key > @p_cursor_tenant_key)
 ORDER BY t.tenant_key ASC
 LIMIT @p_take;

SELECT CASE WHEN @p_include_total IS NOT NULL THEN (
         SELECT COUNT(*)
           FROM {{schema}}.tenants t
          WHERE (@p_search IS NULL OR instr(LOWER(t.tenant_key), @p_search) > 0 OR instr(LOWER(t.display_name), @p_search) > 0 OR instr(LOWER(t.description), @p_search) > 0)
            AND (@p_status_code IS NULL OR t.status_code = @p_status_code)
            AND (@p_tag_filters IS NULL OR NOT EXISTS (
                 SELECT 1 FROM json_each(@p_tag_filters) f
                  WHERE NOT EXISTS (
                        SELECT 1 FROM {{schema}}.tags tg
                         WHERE tg.scope_code = 20 /* TagScopeCode.Tenant */ AND tg.scope_id = t.id
                           AND tg.name = json_extract(f.value, '$.name')
                           AND (json_extract(f.value, '$.value_search') IS NULL OR tg.value_search = json_extract(f.value, '$.value_search'))
                  )
                 ))
       ) END;
