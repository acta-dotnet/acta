SELECT e.id, e.event_code, e.created_at_utc, ns.name, e.job_id, e.lineage_root_id,
       e.definition_id, e.worker_id, e.execution_number, e.actor_code, e.actor_key,
       e.from_status_code, e.to_status_code, e.execution_status_code, e.duration_ms,
       e.reason_code, e.reason_message,
       e.job_ref, rjob.job_ref AS lineage_root_job_ref,
       e.tenant_id,
       e.detail_format_id, e.detail
  FROM {{schema}}.events e
  JOIN {{schema}}.namespaces ns ON ns.id = e.namespace_id
  LEFT JOIN {{schema}}.jobs rjob ON rjob.id = e.lineage_root_id
 WHERE (@p_job_id IS NULL OR e.job_id = @p_job_id)
   AND (@p_lineage_root_id IS NULL OR e.lineage_root_id = @p_lineage_root_id)
   AND (@p_namespace_name IS NULL OR ns.name = @p_namespace_name)
   AND (@p_event_code IS NULL OR e.event_code = @p_event_code)
   AND (@p_definition_id IS NULL OR e.definition_id = @p_definition_id)
   AND (@p_tenant_id IS NULL OR e.tenant_id = @p_tenant_id)
   AND (@p_worker_id IS NULL OR e.worker_id = @p_worker_id)
   AND (@p_actor_code IS NULL OR e.actor_code = @p_actor_code)
   AND (@p_reason_code IS NULL OR e.reason_code = @p_reason_code)
   AND (@p_created_from_utc IS NULL OR e.created_at_utc >= @p_created_from_utc)
   AND (@p_created_to_utc IS NULL OR e.created_at_utc < @p_created_to_utc)
AND (@p_tag_filters IS NULL OR NOT EXISTS (
        SELECT 1
          FROM jsonb_array_elements(@p_tag_filters::jsonb) AS f(value)
         WHERE NOT EXISTS (
               SELECT 1 FROM {{schema}}.tags tg
                WHERE tg.scope_code = 90 /* TagScopeCode.Event */ AND tg.scope_id = e.id
                  AND tg.name = f.value->>'name'
                  AND ((f.value->>'value_search') IS NULL OR tg.value_search = f.value->>'value_search')
         )
        ))
      AND (@p_cursor_created_at_utc IS NULL
        OR e.created_at_utc < @p_cursor_created_at_utc
        OR (e.created_at_utc = @p_cursor_created_at_utc AND e.id < @p_cursor_id))
 ORDER BY e.created_at_utc DESC, e.id DESC
 LIMIT @p_take;

SELECT CASE WHEN @p_include_total IS NOT NULL THEN (
         SELECT COUNT(*)
           FROM {{schema}}.events e
           JOIN {{schema}}.namespaces ns ON ns.id = e.namespace_id
          WHERE (@p_job_id IS NULL OR e.job_id = @p_job_id)
            AND (@p_lineage_root_id IS NULL OR e.lineage_root_id = @p_lineage_root_id)
            AND (@p_namespace_name IS NULL OR ns.name = @p_namespace_name)
            AND (@p_event_code IS NULL OR e.event_code = @p_event_code)
            AND (@p_definition_id IS NULL OR e.definition_id = @p_definition_id)
            AND (@p_tenant_id IS NULL OR e.tenant_id = @p_tenant_id)
            AND (@p_worker_id IS NULL OR e.worker_id = @p_worker_id)
            AND (@p_actor_code IS NULL OR e.actor_code = @p_actor_code)
            AND (@p_reason_code IS NULL OR e.reason_code = @p_reason_code)
            AND (@p_created_from_utc IS NULL OR e.created_at_utc >= @p_created_from_utc)
            AND (@p_created_to_utc IS NULL OR e.created_at_utc < @p_created_to_utc)
            AND (@p_tag_filters IS NULL OR NOT EXISTS (
                 SELECT 1
                   FROM jsonb_array_elements(@p_tag_filters::jsonb) AS f(value)
                  WHERE NOT EXISTS (
                        SELECT 1 FROM {{schema}}.tags tg
                         WHERE tg.scope_code = 90 /* TagScopeCode.Event */ AND tg.scope_id = e.id
                           AND tg.name = f.value->>'name'
                           AND ((f.value->>'value_search') IS NULL OR tg.value_search = f.value->>'value_search')
                  )
                 ))
       ) END;
