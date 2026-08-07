SELECT
    ACTA_ERROR(
        'ACTA:ENQ_ROUTE_UNKNOWN:Enqueue rejected: one or more rows reference an unknown'
        || ' namespace or job. Has the owning worker run InitializeAsync yet?'
    )
WHERE EXISTS (
    SELECT 1 FROM JSON_EACH(@p_rows) r
    WHERE NOT EXISTS (
        SELECT 1 FROM {{schema}}.namespaces ns
        JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.id AND jd.name = JSON_EXTRACT(r.value, '$.job_name')
        WHERE ns.name = JSON_EXTRACT(r.value, '$.namespace_name')
    )
);

SELECT ACTA_ERROR('ACTA:ENQ_NS_SUSPENDED:Enqueue rejected: one or more rows reference a suspended namespace.')
WHERE EXISTS (
    SELECT 1 FROM JSON_EACH(@p_rows) r
    JOIN {{schema}}.namespaces ns ON ns.name = JSON_EXTRACT(r.value, '$.namespace_name')
    WHERE ns.status_code <> 10 /* JobNamespaceStatusCode.Active */
);

SELECT ACTA_ERROR('ACTA:ENQ_DEF_RETIRED:Enqueue rejected: the job definition is retired.')
WHERE EXISTS (
    SELECT 1 FROM JSON_EACH(@p_rows) r
    JOIN {{schema}}.namespaces ns ON ns.name = JSON_EXTRACT(r.value, '$.namespace_name')
    JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.id AND jd.name = JSON_EXTRACT(r.value, '$.job_name')
    WHERE jd.status_code <> 10 /* JobDefinitionStatusCode.Active */
);

SELECT ACTA_ERROR('Enqueue rejected: one or more child rows reference a missing or terminal parent job.')
WHERE EXISTS (
    SELECT 1 FROM JSON_EACH(@p_rows) r
    WHERE
        JSON_EXTRACT(r.value, '$.parent_id') IS NOT NULL
        AND NOT EXISTS (
            SELECT 1 FROM {{schema}}.jobs pj
            JOIN {{schema}}.runtimes pr ON pr.job_id = pj.id
            WHERE
                pj.id = JSON_EXTRACT(r.value, '$.parent_id')
                AND pr.status_code NOT IN (
                    100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */
                )
        )
);

SELECT ACTA_ERROR('ACTA:ENQ_TENANT_UNKNOWN:Enqueue rejected: one or more rows reference an unknown tenant.')
WHERE EXISTS (
    SELECT 1 FROM JSON_EACH(@p_rows) r
    WHERE
        JSON_EXTRACT(r.value, '$.tenant_key') IS NOT NULL
        AND NOT EXISTS (
            SELECT 1 FROM {{schema}}.tenants t
            WHERE t.tenant_key = JSON_EXTRACT(r.value, '$.tenant_key')
        )
);

SELECT ACTA_ERROR('ACTA:ENQ_TENANT_SUSPENDED:Enqueue rejected: one or more rows reference a suspended tenant.')
WHERE EXISTS (
    SELECT 1 FROM JSON_EACH(@p_rows) r
    JOIN {{schema}}.tenants t ON t.tenant_key = JSON_EXTRACT(r.value, '$.tenant_key')
    WHERE t.status_code <> 10 /* TenantStatusCode.Active */
);

SELECT ACTA_ERROR('ACTA:ENQ_TENANT_REQUIRED:Enqueue rejected: one or more rows target a definition that requires a tenant and carry none.')
WHERE EXISTS (
    SELECT 1 FROM JSON_EACH(@p_rows) r
    JOIN {{schema}}.namespaces ns ON ns.name = JSON_EXTRACT(r.value, '$.namespace_name')
    JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.id AND jd.name = JSON_EXTRACT(r.value, '$.job_name')
    WHERE
        jd.tenant_requirement_code = 10 /* JobTenantRequirementCode.Required */
        AND JSON_EXTRACT(r.value, '$.tenant_key') IS NULL
        AND NOT EXISTS (
            SELECT 1 FROM {{schema}}.jobs pj
            WHERE pj.id = JSON_EXTRACT(r.value, '$.parent_id') AND pj.tenant_id IS NOT NULL
        )
);

SELECT ACTA_ERROR('ACTA:ENQ_TENANT_FORBIDDEN:Enqueue rejected: one or more rows target a definition that forbids a tenant and name one.')
WHERE EXISTS (
    SELECT 1 FROM JSON_EACH(@p_rows) r
    JOIN {{schema}}.namespaces ns ON ns.name = JSON_EXTRACT(r.value, '$.namespace_name')
    JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.id AND jd.name = JSON_EXTRACT(r.value, '$.job_name')
    WHERE
        jd.tenant_requirement_code = 20 /* JobTenantRequirementCode.Forbidden */
        AND JSON_EXTRACT(r.value, '$.tenant_key') IS NOT NULL
);

SELECT
    ACTA_ERROR(
        'ACTA:ENQ_TENANT_MISMATCH:Enqueue rejected: one or more child rows name a TenantKey'
        || ' that differs from the parent tenant without an explicit override.'
    )
WHERE EXISTS (
    SELECT 1 FROM JSON_EACH(@p_rows) r
    JOIN {{schema}}.tenants t ON t.tenant_key = JSON_EXTRACT(r.value, '$.tenant_key')
    JOIN {{schema}}.jobs pj ON pj.id = JSON_EXTRACT(r.value, '$.parent_id')
    WHERE
        JSON_EXTRACT(r.value, '$.tenant_override') = 0
        AND pj.tenant_id IS NOT NULL
        AND pj.tenant_id <> t.id
);

INSERT INTO {{schema}}.jobs (
    job_ref, lineage_root_id, parent_id, deduplication_key, correlation_key,
    namespace_id, definition_id, tenant_id,
    input_format_id, input, exclusive_key, audit_level_code
)
SELECT
    JSON_EXTRACT(r.value, '$.job_ref'),
    NULL,
    NULL,
    JSON_EXTRACT(r.value, '$.deduplication_key'),
    JSON_EXTRACT(r.value, '$.correlation_key'),
    ns.id,
    jd.id,
    (
        SELECT t.id FROM {{schema}}.tenants t
        WHERE t.tenant_key = JSON_EXTRACT(r.value, '$.tenant_key') AND t.status_code = 10 /* TenantStatusCode.Active */
    ),
    JSON_EXTRACT(r.value, '$.input_format_id'),
    ACTA_BLOB(JSON_EXTRACT(r.value, '$.input')),
    JSON_EXTRACT(r.value, '$.exclusive_key'),
    jd.audit_level_code_effective
FROM JSON_EACH(@p_rows) r
JOIN {{schema}}.namespaces ns ON ns.name = JSON_EXTRACT(r.value, '$.namespace_name')
JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.id AND jd.name = JSON_EXTRACT(r.value, '$.job_name')
WHERE JSON_EXTRACT(r.value, '$.parent_id') IS NULL
ON CONFLICT (namespace_id, deduplication_key) WHERE deduplication_key IS NOT NULL AND parent_id IS NULL DO NOTHING;

INSERT INTO {{schema}}.jobs (
    job_ref, lineage_root_id, parent_id, deduplication_key, correlation_key,
    namespace_id, definition_id, tenant_id,
    input_format_id, input, exclusive_key, audit_level_code
)
SELECT
    JSON_EXTRACT(r.value, '$.job_ref'),
    COALESCE(pj.lineage_root_id, pj.id),
    JSON_EXTRACT(r.value, '$.parent_id'),
    JSON_EXTRACT(r.value, '$.deduplication_key'),
    COALESCE(JSON_EXTRACT(r.value, '$.correlation_key'), pj.correlation_key),
    ns.id,
    jd.id,
    CASE
        WHEN jd.tenant_requirement_code = 20 /* JobTenantRequirementCode.Forbidden */ THEN NULL
        ELSE COALESCE(
            (
                SELECT t.id FROM {{schema}}.tenants t
                WHERE t.tenant_key = JSON_EXTRACT(r.value, '$.tenant_key') AND t.status_code = 10 /* TenantStatusCode.Active */
            ),
            pj.tenant_id
        )
    END,
    JSON_EXTRACT(r.value, '$.input_format_id'),
    ACTA_BLOB(JSON_EXTRACT(r.value, '$.input')),
    JSON_EXTRACT(r.value, '$.exclusive_key'),
    jd.audit_level_code_effective
FROM JSON_EACH(@p_rows) r
JOIN {{schema}}.namespaces ns ON ns.name = JSON_EXTRACT(r.value, '$.namespace_name')
JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.id AND jd.name = JSON_EXTRACT(r.value, '$.job_name')
JOIN {{schema}}.jobs pj ON pj.id = JSON_EXTRACT(r.value, '$.parent_id')
WHERE JSON_EXTRACT(r.value, '$.parent_id') IS NOT NULL
ON CONFLICT (parent_id, deduplication_key) WHERE deduplication_key IS NOT NULL AND parent_id IS NOT NULL DO NOTHING;

INSERT INTO {{schema}}.runtimes (
    job_id,
    namespace_id,
    status_code,
    priority_code,
    next_run_at_utc,
    execution_number,
    failure_count,
    retention_until_utc,
    modified_at_utc,
    version
)
SELECT
    j.id,
    j.namespace_id,
    10 /* JobStatusCode.Ready */,
    COALESCE(JSON_EXTRACT(r.value, '$.priority_override'), jd.priority_code_effective),
    COALESCE(
        JSON_EXTRACT(r.value, '$.next_run_at_utc'),
        {{now}} + (COALESCE(JSON_EXTRACT(r.value, '$.delay_seconds'), 0)) * 1000
    ),
    0,
    0,
    NULL,
    {{now}},
    0
FROM JSON_EACH(@p_rows) r
JOIN {{schema}}.jobs j ON j.job_ref = JSON_EXTRACT(r.value, '$.job_ref')
JOIN {{schema}}.definitions jd ON jd.id = j.definition_id
WHERE NOT EXISTS (
    SELECT 1 FROM {{schema}}.runtimes x
    WHERE x.job_id = j.id
);

INSERT INTO {{schema}}.tags (scope_code, scope_id, namespace_id, name, value, value_search)
SELECT
    50 /* TagScopeCode.Job */,
    j.id,
    j.namespace_id,
    JSON_EXTRACT(t.value, '$.name'),
    JSON_EXTRACT(t.value, '$.value'),
    JSON_EXTRACT(t.value, '$.value_search')
FROM JSON_EACH(@p_tags) t
JOIN {{schema}}.jobs j ON j.job_ref = JSON_EXTRACT(t.value, '$.job_ref');

SELECT
    JSON_EXTRACT(r.value, '$.ordinal') AS ordinal,
    COALESCE(ins.id, ex.id, exc.id) AS job_id,
    COALESCE(ins.job_ref, ex.job_ref, exc.job_ref) AS job_ref,
    CASE
        WHEN ins.id IS NOT NULL
            THEN 1 /* JobEnqueueAction.Inserted */
        ELSE 2 /* JobEnqueueAction.Deduplicated */
    END AS action
FROM JSON_EACH(@p_rows) r
LEFT JOIN {{schema}}.jobs ins ON ins.job_ref = JSON_EXTRACT(r.value, '$.job_ref')
LEFT JOIN {{schema}}.jobs ex
    ON
        JSON_EXTRACT(r.value, '$.deduplication_key') IS NOT NULL
        AND JSON_EXTRACT(r.value, '$.parent_id') IS NULL
        AND ex.parent_id IS NULL
        AND ex.deduplication_key = JSON_EXTRACT(r.value, '$.deduplication_key')
        AND ex.namespace_id = (
            SELECT ns.id FROM {{schema}}.namespaces ns
            WHERE ns.name = JSON_EXTRACT(r.value, '$.namespace_name')
        )
LEFT JOIN {{schema}}.jobs exc
    ON
        JSON_EXTRACT(r.value, '$.deduplication_key') IS NOT NULL
        AND JSON_EXTRACT(r.value, '$.parent_id') IS NOT NULL
        AND exc.parent_id = JSON_EXTRACT(r.value, '$.parent_id')
        AND exc.deduplication_key = JSON_EXTRACT(r.value, '$.deduplication_key')
ORDER BY JSON_EXTRACT(r.value, '$.ordinal');
