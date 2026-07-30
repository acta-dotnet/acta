WITH target(scope_id) AS (
    SELECT CONVERT(BIGINT, t.id)
      FROM {{schema}}.tenants t
     WHERE @p_scope_code = 20 /* TagScopeCode.Tenant */ AND t.tenant_key = @p_lookup_name
    UNION ALL
    SELECT CONVERT(BIGINT, n.id)
      FROM {{schema}}.namespaces n
     WHERE @p_scope_code = 30 /* TagScopeCode.Namespace */ AND n.name = @p_lookup_name
    UNION ALL
    SELECT CONVERT(BIGINT, d.id)
      FROM {{schema}}.definitions d
     WHERE @p_scope_code = 40 /* TagScopeCode.Definition */ AND d.id = @p_lookup_id
    UNION ALL
    SELECT j.id
      FROM {{schema}}.jobs j
     WHERE @p_scope_code = 50 /* TagScopeCode.Job */ AND j.id = @p_lookup_id
    UNION ALL
    SELECT s.id
      FROM {{schema}}.schedules s
     WHERE @p_scope_code = 60 /* TagScopeCode.Schedule */ AND s.job_id = @p_lookup_id AND s.name = @p_lookup_name
    UNION ALL
    SELECT CONVERT(BIGINT, w.id)
      FROM {{schema}}.workers w
     WHERE @p_scope_code = 70 /* TagScopeCode.Worker */ AND w.id = @p_lookup_id
    UNION ALL
    SELECT a.id
      FROM {{schema}}.alerts a
     WHERE @p_scope_code = 80 /* TagScopeCode.Alert */ AND a.id = @p_lookup_id
    UNION ALL
    SELECT e.id
      FROM {{schema}}.events e
     WHERE @p_scope_code = 90 /* TagScopeCode.Event */ AND e.id = @p_lookup_id
)
SELECT tg.name, tg.value
  FROM target x
  LEFT JOIN {{schema}}.tags tg
    ON tg.scope_code = @p_scope_code AND tg.scope_id = x.scope_id
 ORDER BY tg.name;
