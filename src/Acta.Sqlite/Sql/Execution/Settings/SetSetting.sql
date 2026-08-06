-- Scoped upsert, last write wins: targets resolve the scope (none = Global, namespace alone =
-- Namespace, namespace + job name = Definition); unregistered targets are NotFound. The name is
-- validated dotted-kebab upstream, so the detail JSON concatenation cannot break out of the string.
DROP TABLE IF EXISTS temp._set_setting;

CREATE TEMP TABLE _set_setting AS
SELECT
    CASE WHEN @p_namespace_name IS NULL THEN 10 /* SettingScopeCode.Global */
         WHEN @p_job_name IS NULL THEN 30 /* SettingScopeCode.Namespace */
         ELSE 40 /* SettingScopeCode.Definition */ END AS scope_code,
    (SELECT n.id FROM {{schema}}.namespaces n WHERE n.name = @p_namespace_name) AS namespace_id,
    (SELECT d.id FROM {{schema}}.definitions d
       JOIN {{schema}}.namespaces n ON n.id = d.namespace_id
      WHERE n.name = @p_namespace_name AND d.name = @p_job_name) AS definition_id;

INSERT INTO {{schema}}.settings
    (scope_code, scope_id, name, value_format_id, value, description)
SELECT r.scope_code, NULL, @p_name, @p_value_format_id, @p_value, @p_description
  FROM temp._set_setting r
 WHERE r.scope_code = 10 /* SettingScopeCode.Global */
ON CONFLICT (scope_code, name) WHERE scope_id IS NULL DO UPDATE
   SET value_format_id = excluded.value_format_id, value = excluded.value,
       description = excluded.description, modified_at_utc = {{now}}, version = version + 1;

INSERT INTO {{schema}}.settings
    (scope_code, scope_id, name, value_format_id, value, description)
SELECT r.scope_code, COALESCE(r.definition_id, r.namespace_id), @p_name, @p_value_format_id, @p_value, @p_description
  FROM temp._set_setting r
 WHERE (r.scope_code = 30 /* SettingScopeCode.Namespace */ AND r.namespace_id IS NOT NULL)
    OR (r.scope_code = 40 /* SettingScopeCode.Definition */ AND r.definition_id IS NOT NULL)
ON CONFLICT (scope_code, scope_id, name) WHERE scope_id IS NOT NULL DO UPDATE
   SET value_format_id = excluded.value_format_id, value = excluded.value,
       description = excluded.description, modified_at_utc = {{now}}, version = version + 1;

-- namespace_id 1 is the seeded sys namespace (M001); detail identifies the setting by name.
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, actor_code, actor_key,
    job_id, job_ref, execution_number, lineage_root_id, definition_id, tenant_id, worker_id,
    from_status_code, to_status_code, execution_status_code, duration_ms, reason_code, reason_message,
    detail_format_id, detail)
SELECT
    160 /* JobEventCode.SettingUpdated */, {{now}}, COALESCE(r.namespace_id, 1), @p_actor_code, @p_actor_key,
    NULL, NULL, NULL, NULL, r.definition_id, NULL, NULL,
    NULL, NULL, NULL, NULL, NULL, @p_reason_message,
    1 /* JobPayloadFormat.Json */, CAST('{"name":"' || @p_name || '"}' AS BLOB)
  FROM temp._set_setting r
 WHERE r.scope_code = 10 /* SettingScopeCode.Global */
    OR (r.scope_code = 30 /* SettingScopeCode.Namespace */ AND r.namespace_id IS NOT NULL)
    OR (r.scope_code = 40 /* SettingScopeCode.Definition */ AND r.definition_id IS NOT NULL);

SELECT
    CASE WHEN r.scope_code = 10 /* SettingScopeCode.Global */ THEN 1 /* AdminControlAction.Applied */
         WHEN r.scope_code = 30 /* SettingScopeCode.Namespace */ AND r.namespace_id IS NULL THEN 2 /* AdminControlAction.NotFound */
         WHEN r.scope_code = 40 /* SettingScopeCode.Definition */ AND r.definition_id IS NULL THEN 2 /* AdminControlAction.NotFound */
         ELSE 1 /* AdminControlAction.Applied */ END AS action,
    (SELECT s.version FROM {{schema}}.settings s
      WHERE s.scope_code = r.scope_code
        AND s.scope_id IS COALESCE(r.definition_id, CASE WHEN r.scope_code = 30 /* SettingScopeCode.Namespace */ THEN r.namespace_id END)
        AND s.name = @p_name) AS version
  FROM temp._set_setting r;
