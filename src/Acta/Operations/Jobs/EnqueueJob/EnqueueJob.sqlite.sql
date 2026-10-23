SELECT
    ACTA_ERROR(
        'ACTA:ENQ_ROUTE_UNKNOWN:Enqueue rejected: one or more rows reference an unknown'
        || ' namespace or job. Has the owning worker run InitializeAsync yet?'
    )
WHERE NOT EXISTS (
    SELECT 1 FROM {{schema}}.namespaces ns
    JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.namespace_id AND jd.definition_name = @p_job_name
    WHERE ns.namespace_name = @p_namespace_name
);

SELECT ACTA_ERROR('ACTA:ENQ_NS_SUSPENDED:Enqueue rejected: one or more rows reference a suspended namespace.')
WHERE EXISTS (
    SELECT 1 FROM {{schema}}.namespaces ns
    WHERE
        ns.namespace_name = @p_namespace_name
        AND ns.namespace_status_code <> 10 /* NamespaceStatusCode.Active */
);

SELECT ACTA_ERROR('ACTA:ENQ_DEF_RETIRED:Enqueue rejected: the job definition is retired.')
WHERE EXISTS (
    SELECT 1 FROM {{schema}}.namespaces ns
    JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.namespace_id AND jd.definition_name = @p_job_name
    WHERE
        ns.namespace_name = @p_namespace_name
        AND jd.definition_status_code <> 10 /* DefinitionStatusCode.Active */
);

-- begin child-only: a child its parent's handler starts lands only while that attempt holds the parent's row
-- (docs/sql-internals.md, "Execution fence"); a job with no parent carries no parent fence.
SELECT ACTA_ERROR('ACTA:EXECUTION_FENCED:enqueue_job: the parent job is no longer executing this attempt.')
WHERE
    @p_parent_execution_number IS NOT NULL
    AND NOT EXISTS (
        SELECT 1
        FROM {{schema}}.jobs_state fr
        WHERE
            fr.job_id = @p_parent_job_id
            AND fr.job_status_code = 50 /* JobStatusCode.Executing */
            AND fr.execution_number = @p_parent_execution_number
            AND fr.leased_by_worker_id = @p_parent_worker_id
    );
-- end child-only

SELECT ACTA_ERROR('Enqueue rejected: one or more child rows reference a missing or terminal parent job.')
WHERE
    @p_parent_job_id IS NOT NULL
    AND NOT EXISTS (
        SELECT 1 FROM {{schema}}.jobs pj
        JOIN {{schema}}.jobs_state pr ON pr.job_id = pj.job_id
        WHERE
            pj.job_id = @p_parent_job_id
            AND pr.job_status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
    );

SELECT ACTA_ERROR('ACTA:ENQ_TENANT_UNKNOWN:Enqueue rejected: one or more rows reference an unknown tenant.')
WHERE
    @p_tenant_name IS NOT NULL
    AND NOT EXISTS (
        SELECT 1 FROM {{schema}}.tenants t
        WHERE t.tenant_name = @p_tenant_name
    );

SELECT ACTA_ERROR('ACTA:ENQ_TENANT_SUSPENDED:Enqueue rejected: one or more rows reference a suspended tenant.')
WHERE
    @p_tenant_name IS NOT NULL
    AND EXISTS (
        SELECT 1 FROM {{schema}}.tenants t
        WHERE
            t.tenant_name = @p_tenant_name
            AND t.tenant_status_code <> 10 /* TenantStatusCode.Active */
    );

SELECT ACTA_ERROR('ACTA:ENQ_TENANT_REQUIRED:Enqueue rejected: the job definition requires a tenant and the row carries none.')
WHERE
    @p_tenant_name IS NULL
    AND EXISTS (
        SELECT 1 FROM {{schema}}.namespaces ns
        JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.namespace_id AND jd.definition_name = @p_job_name
        WHERE
            ns.namespace_name = @p_namespace_name
            AND jd.tenant_requirement_code = 10 /* DefinitionTenantRequirementCode.Required */
    )
    AND NOT EXISTS (
        SELECT 1 FROM {{schema}}.jobs pj
        WHERE pj.job_id = @p_parent_job_id AND pj.tenant_id IS NOT NULL
    );

SELECT ACTA_ERROR('ACTA:ENQ_TENANT_FORBIDDEN:Enqueue rejected: the job definition forbids a tenant and the row names one.')
WHERE
    @p_tenant_name IS NOT NULL
    AND EXISTS (
        SELECT 1 FROM {{schema}}.namespaces ns
        JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.namespace_id AND jd.definition_name = @p_job_name
        WHERE
            ns.namespace_name = @p_namespace_name
            AND jd.tenant_requirement_code = 20 /* DefinitionTenantRequirementCode.Forbidden */
    );

SELECT
    ACTA_ERROR(
        'ACTA:ENQ_TENANT_MISMATCH:Enqueue rejected: a child TenantName differs from the parent'
        || ' tenant without an explicit override.'
    )
WHERE
    @p_parent_job_id IS NOT NULL
    AND @p_tenant_name IS NOT NULL
    AND @p_tenant_override = 0
    AND EXISTS (
        SELECT 1 FROM {{schema}}.jobs pj
        JOIN {{schema}}.tenants t ON t.tenant_name = @p_tenant_name
        WHERE
            pj.job_id = @p_parent_job_id
            AND pj.tenant_id IS NOT NULL
            AND pj.tenant_id <> t.tenant_id
    );

-- The immediate transaction is the lane's mutex on SQLite: it serializes this enqueue against every
-- settle and enqueue, so Ready versus Blocked below is decided on a stable member set.
INSERT INTO {{schema}}.lanes (namespace_id, lane_name)
SELECT ns.namespace_id, COALESCE(@p_lane_name, jd.lane_name)
FROM {{schema}}.namespaces ns
JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.namespace_id AND jd.definition_name = @p_job_name
WHERE
    ns.namespace_name = @p_namespace_name
    AND COALESCE(@p_lane_name, jd.lane_name) IS NOT NULL
ON CONFLICT (namespace_id, lane_name) DO NOTHING;

-- begin child-only: SqliteDialect.BindEnqueueJob drops this statement for a job with no parent, whose
-- ancestor walk would find nothing, because parsing it is a quarter of an enqueue's cost.
SELECT
    ACTA_ERROR(
        'ACTA:ENQ_ANCESTOR_LANE:Enqueue rejected: a child names the lane of an unfinished ancestor,'
        || ' so it would wait behind the ancestor that waits for it.'
    )
WHERE
    @p_parent_job_id IS NOT NULL
    AND EXISTS (
        WITH RECURSIVE ancestors (job_id, parent_job_id) AS (
            SELECT a.job_id, a.parent_job_id
            FROM {{schema}}.jobs a
            WHERE a.job_id = @p_parent_job_id
            UNION ALL
            SELECT a.job_id, a.parent_job_id
            FROM {{schema}}.jobs a
            JOIN ancestors c ON a.job_id = c.parent_job_id
        )
        SELECT 1
        FROM ancestors c
        JOIN {{schema}}.jobs_state ar ON ar.job_id = c.job_id
        JOIN {{schema}}.lanes l ON l.lane_id = ar.lane_id
        JOIN {{schema}}.namespaces ns ON ns.namespace_id = l.namespace_id AND ns.namespace_name = @p_namespace_name
        JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.namespace_id AND jd.definition_name = @p_job_name
        WHERE
            l.lane_name = COALESCE(@p_lane_name, jd.lane_name)
            AND ar.job_status_code IN (
                10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
            )
    );
-- end child-only

INSERT INTO {{schema}}.jobs (
    job_ref, lineage_root_job_id, parent_job_id, deduplication_key, correlation_key,
    namespace_id, definition_id, tenant_id,
    input_format_id, input, concurrency_key, event_recording_code
)
SELECT
    @p_job_ref,
    NULL,
    NULL,
    @p_deduplication_key,
    @p_correlation_key,
    ns.namespace_id,
    jd.definition_id,
    (
        SELECT t.tenant_id FROM {{schema}}.tenants t
        WHERE t.tenant_name = @p_tenant_name AND t.tenant_status_code = 10 /* TenantStatusCode.Active */
    ),
    @p_input_format_id,
    @p_input,
    @p_concurrency_key,
    jd.event_recording_code_effective
FROM {{schema}}.namespaces ns
JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.namespace_id AND jd.definition_name = @p_job_name
WHERE
    ns.namespace_name = @p_namespace_name
    AND @p_parent_job_id IS NULL
ON CONFLICT (namespace_id, deduplication_key) WHERE deduplication_key IS NOT NULL AND parent_job_id IS NULL DO NOTHING;

INSERT INTO {{schema}}.jobs (
    job_ref, lineage_root_job_id, parent_job_id, deduplication_key, correlation_key,
    namespace_id, definition_id, tenant_id,
    input_format_id, input, concurrency_key, event_recording_code
)
SELECT
    @p_job_ref,
    COALESCE(pj.lineage_root_job_id, pj.job_id),
    @p_parent_job_id,
    @p_deduplication_key,
    COALESCE(@p_correlation_key, pj.correlation_key),
    ns.namespace_id,
    jd.definition_id,
    CASE
        WHEN jd.tenant_requirement_code = 20 /* DefinitionTenantRequirementCode.Forbidden */ THEN NULL
        ELSE COALESCE(
            (
                SELECT t.tenant_id FROM {{schema}}.tenants t
                WHERE t.tenant_name = @p_tenant_name AND t.tenant_status_code = 10 /* TenantStatusCode.Active */
            ),
            pj.tenant_id
        )
    END,
    @p_input_format_id,
    @p_input,
    @p_concurrency_key,
    jd.event_recording_code_effective
FROM {{schema}}.namespaces ns
JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.namespace_id AND jd.definition_name = @p_job_name
JOIN {{schema}}.jobs pj ON pj.job_id = @p_parent_job_id
WHERE
    ns.namespace_name = @p_namespace_name
    AND @p_parent_job_id IS NOT NULL
ON CONFLICT (parent_job_id, deduplication_key) WHERE deduplication_key IS NOT NULL AND parent_job_id IS NOT NULL DO NOTHING;

-- A laned job enters Ready only when no member of its lane is unfinished; otherwise it waits as
-- Blocked, keeping its due instant for when the settle ahead of it promotes it.
INSERT INTO {{schema}}.jobs_state (
    job_id,
    namespace_id,
    lane_id,
    job_status_code,
    job_priority_code,
    next_run_at_utc,
    execution_number,
    failure_count,
    retention_until_utc,
    modified_at_utc,
    version
)
SELECT
    j.job_id,
    j.namespace_id,
    l.lane_id,
    CASE
        WHEN
            l.lane_id IS NOT NULL
            AND EXISTS (
                SELECT 1
                FROM {{schema}}.jobs_state m
                WHERE
                    m.lane_id = l.lane_id
                    AND m.lane_id IS NOT NULL
                    AND m.job_status_code IN (
                        10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                        30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                    )
            )
            THEN 15 /* JobStatusCode.Blocked */
        ELSE 10 /* JobStatusCode.Ready */
    END,
    COALESCE(@p_job_priority_code_override, jd.job_priority_code_effective),
    COALESCE(@p_next_run_at_utc, {{now}} + (COALESCE(@p_delay_seconds, 0)) * 1000),
    0,
    0,
    NULL,
    {{now}},
    0
FROM {{schema}}.jobs j
JOIN {{schema}}.definitions jd ON jd.definition_id = j.definition_id
LEFT JOIN {{schema}}.lanes l ON l.namespace_id = j.namespace_id AND l.lane_name = COALESCE(@p_lane_name, jd.lane_name)
WHERE
    j.job_ref = @p_job_ref
    AND NOT EXISTS (
        SELECT 1 FROM {{schema}}.jobs_state x
        WHERE x.job_id = j.job_id
    );

INSERT INTO {{schema}}.tags (entity_code, entity_id, tag_name, tag_value, tag_value_search)
SELECT
    50 /* EntityCode.Job */,
    j.job_id,
    JSON_EXTRACT(t.value, '$.tag_name'),
    JSON_EXTRACT(t.value, '$.tag_value'),
    JSON_EXTRACT(t.value, '$.tag_value_search')
FROM JSON_EACH(@p_tags) t
JOIN {{schema}}.jobs j ON j.job_ref = @p_job_ref;

SELECT
    0 AS ordinal,
    COALESCE(ins.job_id, ex.job_id, exc.job_id) AS job_id,
    COALESCE(ins.job_ref, ex.job_ref, exc.job_ref) AS job_ref,
    CASE
        WHEN ins.job_id IS NOT NULL
            THEN 1 /* EnqueueJobAction.Inserted */
        ELSE 2 /* EnqueueJobAction.Deduplicated */
    END AS action
FROM (SELECT 1) one
LEFT JOIN {{schema}}.jobs ins ON ins.job_ref = @p_job_ref
LEFT JOIN {{schema}}.jobs ex
    ON
        @p_deduplication_key IS NOT NULL
        AND @p_parent_job_id IS NULL
        AND ex.parent_job_id IS NULL
        AND ex.deduplication_key = @p_deduplication_key
        AND ex.namespace_id = (
            SELECT ns.namespace_id FROM {{schema}}.namespaces ns
            WHERE ns.namespace_name = @p_namespace_name
        )
LEFT JOIN {{schema}}.jobs
    exc ON @p_deduplication_key IS NOT NULL
AND @p_parent_job_id IS NOT NULL
AND exc.parent_job_id = @p_parent_job_id
AND exc.deduplication_key = @p_deduplication_key;
