SELECT
    t.tenant_id,
    t.tenant_name,
    t.display_name,
    t.description,
    t.tenant_status_code AS status,
    t.created_at_utc,
    t.modified_at_utc,
    t.version
FROM {{schema}}.tenants t
WHERE
    (
        @p_search IS NULL
        OR INSTR(LOWER(t.tenant_name), @p_search) > 0
        OR INSTR(LOWER(t.display_name), @p_search) > 0
        OR INSTR(LOWER(t.description), @p_search) > 0
    )
    AND (@p_tenant_status_code IS NULL OR t.tenant_status_code = @p_tenant_status_code)
    AND (@p_tag_filters IS NULL OR NOT EXISTS (
        SELECT 1 FROM JSON_EACH(@p_tag_filters) f
        WHERE NOT EXISTS (
            SELECT 1 FROM {{schema}}.tags tg
            WHERE
                tg.entity_code = 120 /* EntityCode.Tenant */ AND tg.entity_id = t.tenant_id
                AND tg.tag_name = JSON_EXTRACT(f.value, '$.tag_name')
                AND (
                    JSON_EXTRACT(f.value, '$.tag_value_search') IS NULL
                    OR tg.tag_value_search = JSON_EXTRACT(f.value, '$.tag_value_search')
                )
        )
    ))
    AND (@p_cursor_tenant_name IS NULL OR t.tenant_name > @p_cursor_tenant_name)
ORDER BY t.tenant_name ASC
LIMIT @p_take;

SELECT CASE WHEN @p_include_total IS NOT NULL THEN (
    SELECT COUNT(*)
    FROM {{schema}}.tenants t
    WHERE
        (
            @p_search IS NULL
            OR INSTR(LOWER(t.tenant_name), @p_search) > 0
            OR INSTR(LOWER(t.display_name), @p_search) > 0
            OR INSTR(LOWER(t.description), @p_search) > 0
        )
        AND (@p_tenant_status_code IS NULL OR t.tenant_status_code = @p_tenant_status_code)
        AND (@p_tag_filters IS NULL OR NOT EXISTS (
            SELECT 1 FROM JSON_EACH(@p_tag_filters) f
            WHERE NOT EXISTS (
                SELECT 1 FROM {{schema}}.tags tg
                WHERE
                    tg.entity_code = 120 /* EntityCode.Tenant */ AND tg.entity_id = t.tenant_id
                    AND tg.tag_name = JSON_EXTRACT(f.value, '$.tag_name')
                    AND (
                        JSON_EXTRACT(f.value, '$.tag_value_search') IS NULL
                        OR tg.tag_value_search = JSON_EXTRACT(f.value, '$.tag_value_search')
                    )
            )
        ))
) END;
