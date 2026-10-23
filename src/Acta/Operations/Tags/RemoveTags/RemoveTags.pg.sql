-- One batch, one implicit transaction; only the final SELECT returns rows. The target row is locked first,
-- so tag writes on one target run one at a time.
DROP TABLE IF EXISTS pg_temp._remove_tags_target;
CREATE TEMP TABLE _remove_tags_target (entity_id BIGINT NOT NULL) ON COMMIT DROP;

INSERT INTO _remove_tags_target (entity_id)
SELECT t.tenant_id
FROM {{schema}}.tenants t
WHERE @p_entity_code = 120 /* EntityCode.Tenant */ AND t.tenant_name = @p_lookup_name
FOR UPDATE;

INSERT INTO _remove_tags_target (entity_id)
SELECT n.namespace_id
FROM {{schema}}.namespaces n
WHERE @p_entity_code = 80 /* EntityCode.Namespace */ AND n.namespace_name = @p_lookup_name
FOR UPDATE;

INSERT INTO _remove_tags_target (entity_id)
SELECT d.definition_id
FROM {{schema}}.definitions d
WHERE @p_entity_code = 30 /* EntityCode.Definition */ AND d.definition_id = @p_lookup_id
FOR UPDATE;

INSERT INTO _remove_tags_target (entity_id)
SELECT j.job_id
FROM {{schema}}.jobs j
WHERE @p_entity_code = 50 /* EntityCode.Job */ AND j.job_id = @p_lookup_id
FOR UPDATE;

DELETE FROM {{schema}}.tags t
USING _remove_tags_target x CROSS JOIN jsonb_array_elements(@p_items_json::JSONB) item
WHERE t.entity_code = @p_entity_code AND t.entity_id = x.entity_id AND t.tag_name = item->>'tag_name';

SELECT
    CASE WHEN EXISTS (SELECT 1 FROM _remove_tags_target) THEN 1 /* ControlAction.Applied */ ELSE 2 /* ControlAction.NotFound */ END::SMALLINT AS action;
