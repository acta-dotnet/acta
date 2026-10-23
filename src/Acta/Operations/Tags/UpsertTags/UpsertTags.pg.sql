-- One batch, one implicit transaction; only the final SELECT returns rows. The target row is locked first,
-- so tag writes on one target run one at a time.
DROP TABLE IF EXISTS pg_temp._upsert_tags_target;
CREATE TEMP TABLE _upsert_tags_target (entity_id BIGINT NOT NULL) ON COMMIT DROP;

INSERT INTO _upsert_tags_target (entity_id)
SELECT t.tenant_id
FROM {{schema}}.tenants t
WHERE @p_entity_code = 120 /* EntityCode.Tenant */ AND t.tenant_name = @p_lookup_name
FOR UPDATE;

INSERT INTO _upsert_tags_target (entity_id)
SELECT n.namespace_id
FROM {{schema}}.namespaces n
WHERE @p_entity_code = 80 /* EntityCode.Namespace */ AND n.namespace_name = @p_lookup_name
FOR UPDATE;

INSERT INTO _upsert_tags_target (entity_id)
SELECT d.definition_id
FROM {{schema}}.definitions d
WHERE @p_entity_code = 30 /* EntityCode.Definition */ AND d.definition_id = @p_lookup_id
FOR UPDATE;

INSERT INTO _upsert_tags_target (entity_id)
SELECT j.job_id
FROM {{schema}}.jobs j
WHERE @p_entity_code = 50 /* EntityCode.Job */ AND j.job_id = @p_lookup_id
FOR UPDATE;

-- A new name past the cap is refused before anything is written; the anonymous block only raises.
DROP TABLE IF EXISTS pg_temp._upsert_tags_cap;
CREATE TEMP TABLE _upsert_tags_cap (over_cap BOOLEAN NOT NULL) ON COMMIT DROP;

INSERT INTO _upsert_tags_cap (over_cap)
SELECT
    EXISTS (
        SELECT 1
        FROM jsonb_array_elements(@p_items_json::JSONB) item
        WHERE NOT EXISTS (
            SELECT 1 FROM {{schema}}.tags t
            WHERE t.entity_code = @p_entity_code AND t.entity_id = x.entity_id AND t.tag_name = item->>'tag_name'
        )
    )
    AND (SELECT COUNT(*) FROM {{schema}}.tags t WHERE t.entity_code = @p_entity_code AND t.entity_id = x.entity_id) >= 32
FROM _upsert_tags_target x;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_temp._upsert_tags_cap WHERE over_cap) THEN
        RAISE EXCEPTION 'A target may carry at most 32 tags.';
    END IF;
END
$$;

INSERT INTO {{schema}}.tags (entity_code, entity_id, tag_name, tag_value, tag_value_search)
SELECT @p_entity_code, x.entity_id, item->>'tag_name', item->>'tag_value', item->>'tag_value_search'
FROM _upsert_tags_target x
CROSS JOIN jsonb_array_elements(@p_items_json::JSONB) item
ON CONFLICT (entity_code, entity_id, tag_name) DO UPDATE SET
    tag_value = EXCLUDED.tag_value,
    tag_value_search = EXCLUDED.tag_value_search;

SELECT
    CASE WHEN EXISTS (SELECT 1 FROM _upsert_tags_target) THEN 1 /* ControlAction.Applied */ ELSE 2 /* ControlAction.NotFound */ END::SMALLINT AS action;
