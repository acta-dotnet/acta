SELECT j.id, ns.name, jd.name, j.parent_id, j.lineage_root_id, j.deduplication_key, j.correlation_key,
       r.status_code, r.priority_code, j.created_at_utc, r.modified_at_utc,
       r.next_run_at_utc, r.execution_number, r.failure_count,
       j.job_ref, pjob.job_ref AS parent_job_ref, rjob.job_ref AS lineage_root_job_ref,
       j.tenant_id
  FROM {{schema}}.jobs j
  JOIN {{schema}}.runtimes r ON r.job_id = j.id
  JOIN {{schema}}.namespaces ns ON ns.id = j.namespace_id
  JOIN {{schema}}.definitions jd ON jd.id = j.definition_id
  LEFT JOIN {{schema}}.jobs pjob ON pjob.id = j.parent_id
  LEFT JOIN {{schema}}.jobs rjob ON rjob.id = j.lineage_root_id
 WHERE (@p_namespace_name IS NULL OR ns.name = @p_namespace_name)
   AND (@p_status_code IS NULL OR r.status_code = @p_status_code)
   AND (@p_job_name IS NULL OR jd.name = @p_job_name)
   AND (@p_parent_id IS NULL OR j.parent_id = @p_parent_id)
   AND (@p_tenant_id IS NULL OR j.tenant_id = @p_tenant_id)
   AND (@p_correlation_key IS NULL OR j.correlation_key = @p_correlation_key)
   AND (@p_tag_filters IS NULL OR NOT EXISTS (
        SELECT 1
          FROM jsonb_array_elements(@p_tag_filters::jsonb) AS f(value)
         WHERE NOT EXISTS (
               SELECT 1
                 FROM {{schema}}.tags t
                  WHERE t.scope_code = 50 /* TagScopeCode.Job */ AND t.scope_id = j.id
                  AND t.name = f.value->>'name'
                  AND ((f.value->>'value_search') IS NULL OR t.value_search = f.value->>'value_search')
         )
   ))
   AND (@p_cursor_created_at_utc IS NULL
        OR j.created_at_utc < @p_cursor_created_at_utc
        OR (j.created_at_utc = @p_cursor_created_at_utc AND j.id < @p_cursor_id))
 ORDER BY j.created_at_utc DESC, j.id DESC
 LIMIT @p_take;

SELECT CASE WHEN @p_include_total IS NOT NULL THEN (
         SELECT COUNT(*)
           FROM {{schema}}.jobs j
           JOIN {{schema}}.runtimes r ON r.job_id = j.id
           JOIN {{schema}}.namespaces ns ON ns.id = j.namespace_id
           JOIN {{schema}}.definitions jd ON jd.id = j.definition_id
          WHERE (@p_namespace_name IS NULL OR ns.name = @p_namespace_name)
            AND (@p_status_code IS NULL OR r.status_code = @p_status_code)
            AND (@p_job_name IS NULL OR jd.name = @p_job_name)
            AND (@p_parent_id IS NULL OR j.parent_id = @p_parent_id)
            AND (@p_tenant_id IS NULL OR j.tenant_id = @p_tenant_id)
            AND (@p_correlation_key IS NULL OR j.correlation_key = @p_correlation_key)
            AND (@p_tag_filters IS NULL OR NOT EXISTS (
                 SELECT 1
                   FROM jsonb_array_elements(@p_tag_filters::jsonb) AS f(value)
                  WHERE NOT EXISTS (
                        SELECT 1
                          FROM {{schema}}.tags t
                           WHERE t.scope_code = 50 /* TagScopeCode.Job */ AND t.scope_id = j.id
                           AND t.name = f.value->>'name'
                           AND ((f.value->>'value_search') IS NULL OR t.value_search = f.value->>'value_search')
                  )
            ))
       ) END;
