SELECT TOP (@p_take)
       w.id, ns.name, w.status_code, w.host, w.deployment_version,
       w.engine_version, w.dotnet_version, w.process_id, w.max_concurrency,
       w.last_seen_at_utc, w.created_at_utc, w.modified_at_utc
  FROM {{schema}}.workers w
  JOIN {{schema}}.namespaces ns ON ns.id = w.namespace_id
 WHERE (@p_namespace_name IS NULL OR ns.name = @p_namespace_name)
   AND (@p_status_code IS NULL OR w.status_code = @p_status_code)
AND (@p_tag_filters IS NULL OR NOT EXISTS (
        SELECT 1
          FROM OPENJSON(@p_tag_filters)
               WITH (name VARCHAR(128) '$.name', value_search NVARCHAR(128) '$.value_search') f
         WHERE NOT EXISTS (
               SELECT 1 FROM {{schema}}.tags tg
                WHERE tg.scope_code = 70 /* TagScopeCode.Worker */ AND tg.scope_id = w.id
                  AND tg.name = f.name
                  AND (f.value_search IS NULL OR tg.value_search = f.value_search)
         )
        ))
      AND (@p_cursor_last_seen_at_utc IS NULL
        OR w.last_seen_at_utc < @p_cursor_last_seen_at_utc
        OR (w.last_seen_at_utc = @p_cursor_last_seen_at_utc AND w.id < @p_cursor_int_id))
 ORDER BY w.last_seen_at_utc DESC, w.id DESC
 OPTION (RECOMPILE);

SELECT CASE WHEN @p_include_total IS NOT NULL THEN (
         SELECT COUNT(*)
           FROM {{schema}}.workers w
           JOIN {{schema}}.namespaces ns ON ns.id = w.namespace_id
          WHERE (@p_namespace_name IS NULL OR ns.name = @p_namespace_name)
            AND (@p_status_code IS NULL OR w.status_code = @p_status_code)
            AND (@p_tag_filters IS NULL OR NOT EXISTS (
                 SELECT 1
                   FROM OPENJSON(@p_tag_filters)
                        WITH (name VARCHAR(128) '$.name', value_search NVARCHAR(128) '$.value_search') f
                  WHERE NOT EXISTS (
                        SELECT 1 FROM {{schema}}.tags tg
                         WHERE tg.scope_code = 70 /* TagScopeCode.Worker */ AND tg.scope_id = w.id
                           AND tg.name = f.name
                           AND (f.value_search IS NULL OR tg.value_search = f.value_search)
                  )
                 ))
       ) END
 OPTION (RECOMPILE);
