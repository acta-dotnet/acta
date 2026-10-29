SELECT
    ACTA_ERROR(
        'ACTA:ENQ_ROUTE_UNKNOWN:Enqueue rejected: one or more rows reference an unknown'
        || ' namespace or job. Has the owning worker run InitializeAsync yet?'
    )
WHERE EXISTS (
    SELECT 1 FROM JSON_EACH(@p_rows) r
    WHERE NOT EXISTS (
        SELECT 1 FROM {{schema}}.namespaces ns
        JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.namespace_id AND jd.definition_name = JSON_EXTRACT(r.value, '$.job_name')
        WHERE ns.namespace_name = JSON_EXTRACT(r.value, '$.namespace_name')
    )
);

SELECT ACTA_ERROR('ACTA:ENQ_NS_SUSPENDED:Enqueue rejected: one or more rows reference a suspended namespace.')
WHERE EXISTS (
    SELECT 1 FROM JSON_EACH(@p_rows) r
    JOIN {{schema}}.namespaces ns ON ns.namespace_name = JSON_EXTRACT(r.value, '$.namespace_name')
    WHERE ns.namespace_status_code <> 10 /* NamespaceStatusCode.Active */
);

SELECT ACTA_ERROR('ACTA:ENQ_DEF_RETIRED:Enqueue rejected: the job definition is retired.')
WHERE EXISTS (
    SELECT 1 FROM JSON_EACH(@p_rows) r
    JOIN {{schema}}.namespaces ns ON ns.namespace_name = JSON_EXTRACT(r.value, '$.namespace_name')
    JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.namespace_id AND jd.definition_name = JSON_EXTRACT(r.value, '$.job_name')
    WHERE jd.definition_status_code <> 10 /* DefinitionStatusCode.Active */
);

SELECT ACTA_ERROR('Enqueue rejected: one or more child rows reference a missing or terminal parent job.')
WHERE EXISTS (
    SELECT 1 FROM JSON_EACH(@p_rows) r
    WHERE
        JSON_EXTRACT(r.value, '$.parent_job_id') IS NOT NULL
        AND NOT EXISTS (
            SELECT 1 FROM {{schema}}.jobs pj
            JOIN {{schema}}.jobs_state pr ON pr.job_id = pj.job_id
            WHERE
                pj.job_id = JSON_EXTRACT(r.value, '$.parent_job_id')
                AND pr.job_status_code NOT IN (
                    100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */
                )
        )
);

SELECT ACTA_ERROR('ACTA:ENQ_TENANT_UNKNOWN:Enqueue rejected: one or more rows reference an unknown tenant.')
WHERE EXISTS (
    SELECT 1 FROM JSON_EACH(@p_rows) r
    WHERE
        JSON_EXTRACT(r.value, '$.tenant_name') IS NOT NULL
        AND NOT EXISTS (
            SELECT 1 FROM {{schema}}.tenants t
            WHERE t.tenant_name = JSON_EXTRACT(r.value, '$.tenant_name')
        )
);

SELECT ACTA_ERROR('ACTA:ENQ_TENANT_SUSPENDED:Enqueue rejected: one or more rows reference a suspended tenant.')
WHERE EXISTS (
    SELECT 1 FROM JSON_EACH(@p_rows) r
    JOIN {{schema}}.tenants t ON t.tenant_name = JSON_EXTRACT(r.value, '$.tenant_name')
    WHERE t.tenant_status_code <> 10 /* TenantStatusCode.Active */
);

SELECT ACTA_ERROR('ACTA:ENQ_TENANT_REQUIRED:Enqueue rejected: one or more rows target a definition that requires a tenant and carry none.')
WHERE EXISTS (
    SELECT 1 FROM JSON_EACH(@p_rows) r
    JOIN {{schema}}.namespaces ns ON ns.namespace_name = JSON_EXTRACT(r.value, '$.namespace_name')
    JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.namespace_id AND jd.definition_name = JSON_EXTRACT(r.value, '$.job_name')
    WHERE
        jd.tenant_requirement_code = 10 /* DefinitionTenantRequirementCode.Required */
        AND JSON_EXTRACT(r.value, '$.tenant_name') IS NULL
        AND NOT EXISTS (
            SELECT 1 FROM {{schema}}.jobs pj
            WHERE pj.job_id = JSON_EXTRACT(r.value, '$.parent_job_id') AND pj.tenant_id IS NOT NULL
        )
);

SELECT ACTA_ERROR('ACTA:ENQ_TENANT_FORBIDDEN:Enqueue rejected: one or more rows target a definition that forbids a tenant and name one.')
WHERE EXISTS (
    SELECT 1 FROM JSON_EACH(@p_rows) r
    JOIN {{schema}}.namespaces ns ON ns.namespace_name = JSON_EXTRACT(r.value, '$.namespace_name')
    JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.namespace_id AND jd.definition_name = JSON_EXTRACT(r.value, '$.job_name')
    WHERE
        jd.tenant_requirement_code = 20 /* DefinitionTenantRequirementCode.Forbidden */
        AND JSON_EXTRACT(r.value, '$.tenant_name') IS NOT NULL
);

SELECT
    ACTA_ERROR(
        'ACTA:ENQ_TENANT_MISMATCH:Enqueue rejected: one or more child rows name a TenantName'
        || ' that differs from the parent tenant without an explicit override.'
    )
WHERE EXISTS (
    SELECT 1 FROM JSON_EACH(@p_rows) r
    JOIN {{schema}}.tenants t ON t.tenant_name = JSON_EXTRACT(r.value, '$.tenant_name')
    JOIN {{schema}}.jobs pj ON pj.job_id = JSON_EXTRACT(r.value, '$.parent_job_id')
    WHERE
        JSON_EXTRACT(r.value, '$.tenant_override') = 0
        AND pj.tenant_id IS NOT NULL
        AND pj.tenant_id <> t.tenant_id
);

-- The immediate transaction is the lanes' mutex on SQLite: it serializes this batch against every
-- settle and enqueue, so Ready versus Blocked below is decided on a stable member set.
INSERT INTO {{schema}}.lanes (namespace_id, lane_name)
SELECT DISTINCT ns.namespace_id, COALESCE(JSON_EXTRACT(r.value, '$.lane_name'), jd.lane_name)
FROM JSON_EACH(@p_rows) r
JOIN {{schema}}.namespaces ns ON ns.namespace_name = JSON_EXTRACT(r.value, '$.namespace_name')
JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.namespace_id AND jd.definition_name = JSON_EXTRACT(r.value, '$.job_name')
WHERE COALESCE(JSON_EXTRACT(r.value, '$.lane_name'), jd.lane_name) IS NOT NULL
ON CONFLICT (namespace_id, lane_name) DO NOTHING;

SELECT
    ACTA_ERROR(
        'ACTA:ENQ_ANCESTOR_LANE:Enqueue rejected: one or more child rows name the lane of an unfinished'
        || ' ancestor, so they would wait behind the ancestor that waits for them.'
    )
WHERE EXISTS (
    WITH RECURSIVE ancestors (lane_id, job_id, parent_job_id) AS (
        SELECT l.lane_id, a.job_id, a.parent_job_id
        FROM JSON_EACH(@p_rows) r
        JOIN {{schema}}.namespaces ns ON ns.namespace_name = JSON_EXTRACT(r.value, '$.namespace_name')
        JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.namespace_id AND jd.definition_name = JSON_EXTRACT(r.value, '$.job_name')
        JOIN {{schema}}.lanes l
            ON
                l.namespace_id = ns.namespace_id
                AND l.lane_name = COALESCE(JSON_EXTRACT(r.value, '$.lane_name'), jd.lane_name)
        JOIN {{schema}}.jobs a ON a.job_id = JSON_EXTRACT(r.value, '$.parent_job_id')
        UNION ALL
        SELECT c.lane_id, a.job_id, a.parent_job_id
        FROM {{schema}}.jobs a
        JOIN ancestors c ON a.job_id = c.parent_job_id
    )
    SELECT 1
    FROM ancestors c
    JOIN {{schema}}.jobs_state ar ON ar.job_id = c.job_id
    WHERE
        ar.lane_id = c.lane_id
        AND ar.job_status_code IN (
            10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
            30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
        )
);

-- One ordered insert for roots and children alike, so job-id order equals the caller's batch order,
-- which is the order a lane runs in. The untargeted DO NOTHING skips a row that repeats a
-- deduplication key under either unique index.
INSERT INTO {{schema}}.jobs (
    job_kind_code, job_ref, lineage_root_job_id, parent_job_id, deduplication_key, correlation_key,
    namespace_id, definition_id, tenant_id,
    input_format_id, input, concurrency_key, event_recording_code
)
SELECT
    10 /* JobKindCode.Ordinary */,
    JSON_EXTRACT(r.value, '$.job_ref'),
    CASE WHEN pj.job_id IS NOT NULL THEN COALESCE(pj.lineage_root_job_id, pj.job_id) END,
    pj.job_id,
    JSON_EXTRACT(r.value, '$.deduplication_key'),
    COALESCE(JSON_EXTRACT(r.value, '$.correlation_key'), pj.correlation_key),
    ns.namespace_id,
    jd.definition_id,
    CASE
        WHEN jd.tenant_requirement_code = 20 /* DefinitionTenantRequirementCode.Forbidden */ THEN NULL
        ELSE COALESCE(
            (
                SELECT t.tenant_id FROM {{schema}}.tenants t
                WHERE t.tenant_name = JSON_EXTRACT(r.value, '$.tenant_name') AND t.tenant_status_code = 10 /* TenantStatusCode.Active */
            ),
            pj.tenant_id
        )
    END,
    JSON_EXTRACT(r.value, '$.input_format_id'),
    CASE
        WHEN JSON_EXTRACT(r.value, '$.input_format_id') <> 0 /* JobPayloadFormat.None */
            THEN COALESCE(ACTA_BLOB(JSON_EXTRACT(r.value, '$.input')), X'')
    END,
    JSON_EXTRACT(r.value, '$.concurrency_key'),
    jd.event_recording_code_effective
FROM JSON_EACH(@p_rows) r
JOIN {{schema}}.namespaces ns ON ns.namespace_name = JSON_EXTRACT(r.value, '$.namespace_name')
JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.namespace_id AND jd.definition_name = JSON_EXTRACT(r.value, '$.job_name')
LEFT JOIN {{schema}}.jobs pj ON pj.job_id = JSON_EXTRACT(r.value, '$.parent_job_id')
WHERE
    JSON_EXTRACT(r.value, '$.parent_job_id') IS NULL
    OR pj.job_id IS NOT NULL
ORDER BY JSON_EXTRACT(r.value, '$.ordinal')
-- Only a deduplication hit is absorbed; any other conflict, a taken job_ref included, raises.
ON CONFLICT (namespace_id, deduplication_key) WHERE deduplication_key IS NOT NULL AND parent_job_id IS NULL DO NOTHING
ON CONFLICT (parent_job_id, deduplication_key) WHERE deduplication_key IS NOT NULL AND parent_job_id IS NOT NULL DO NOTHING;

-- A laned row enters Ready only as the first inserted row of its lane with no unfinished member
-- already there; every later one waits as Blocked. The existence probe reads the table before this
-- statement's own rows land, so the batch's rows are ranked by the window instead.
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
            AND (
                ROW_NUMBER() OVER (PARTITION BY l.lane_id ORDER BY j.job_id) > 1
                OR EXISTS (
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
            )
            THEN 15 /* JobStatusCode.Blocked */
        ELSE 10 /* JobStatusCode.Ready */
    END,
    COALESCE(JSON_EXTRACT(r.value, '$.job_priority_code_override'), jd.job_priority_code_effective),
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
JOIN {{schema}}.definitions jd ON jd.definition_id = j.definition_id
LEFT JOIN {{schema}}.lanes l
    ON
        l.namespace_id = j.namespace_id
        AND l.lane_name = COALESCE(JSON_EXTRACT(r.value, '$.lane_name'), jd.lane_name)
WHERE NOT EXISTS (
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
JOIN {{schema}}.jobs j ON j.job_ref = JSON_EXTRACT(t.value, '$.job_ref');

SELECT
    JSON_EXTRACT(r.value, '$.ordinal') AS ordinal,
    COALESCE(ins.job_id, ex.job_id, exc.job_id) AS job_id,
    COALESCE(ins.job_ref, ex.job_ref, exc.job_ref) AS job_ref,
    CASE
        WHEN ins.job_id IS NOT NULL
            THEN 1 /* EnqueueJobAction.Inserted */
        ELSE 2 /* EnqueueJobAction.Deduplicated */
    END AS action
FROM JSON_EACH(@p_rows) r
LEFT JOIN {{schema}}.jobs ins ON ins.job_ref = JSON_EXTRACT(r.value, '$.job_ref')
LEFT JOIN {{schema}}.jobs ex
    ON
        JSON_EXTRACT(r.value, '$.deduplication_key') IS NOT NULL
        AND JSON_EXTRACT(r.value, '$.parent_job_id') IS NULL
        AND ex.parent_job_id IS NULL
        AND ex.deduplication_key = JSON_EXTRACT(r.value, '$.deduplication_key')
        AND ex.namespace_id = (
            SELECT ns.namespace_id FROM {{schema}}.namespaces ns
            WHERE ns.namespace_name = JSON_EXTRACT(r.value, '$.namespace_name')
        )
LEFT JOIN {{schema}}.jobs exc
    ON
        JSON_EXTRACT(r.value, '$.deduplication_key') IS NOT NULL
        AND JSON_EXTRACT(r.value, '$.parent_job_id') IS NOT NULL
        AND exc.parent_job_id = JSON_EXTRACT(r.value, '$.parent_job_id')
        AND exc.deduplication_key = JSON_EXTRACT(r.value, '$.deduplication_key')
ORDER BY JSON_EXTRACT(r.value, '$.ordinal');
