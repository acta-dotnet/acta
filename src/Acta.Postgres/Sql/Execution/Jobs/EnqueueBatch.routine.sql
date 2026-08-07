CREATE OR REPLACE FUNCTION {{schema}}.enqueue_batch(
    p_b_ordinal INT [],
    p_b_job_ref UUID [],
    p_b_namespace_name VARCHAR [],
    p_b_job_name VARCHAR [],
    p_b_deduplication_key VARCHAR [],
    p_b_correlation_key VARCHAR [],
    p_b_priority_override SMALLINT [],
    p_b_input_format_id SMALLINT [],
    p_b_input BYTEA [],
    p_b_exclusive_key VARCHAR [],
    p_b_next_run_at_utc TIMESTAMPTZ [],
    p_b_delay_seconds INT [],
    p_b_parent_id BIGINT [],
    p_b_tenant_key VARCHAR [],
    p_b_tenant_override BOOLEAN [],
    p_t_ordinal INT [],
    p_t_name VARCHAR [],
    p_t_value VARCHAR [],
    p_t_value_search VARCHAR []
)
RETURNS TABLE (ordinal INT, job_id BIGINT, job_ref UUID, action INT)
LANGUAGE plpgsql
AS $$
DECLARE
    batch_count INT;
    resolved_count INT;
    ns_active_count INT;
    parent_count INT;
    parent_live INT;
BEGIN

    batch_count := COALESCE(array_length(p_b_ordinal, 1), 0);

    SELECT COUNT(*)
    INTO resolved_count
    FROM unnest(p_b_namespace_name, p_b_job_name) AS b(namespace_name, job_name)
    INNER JOIN {{schema}}.namespaces ns ON ns.name = b.namespace_name
    INNER JOIN {{schema}}.definitions jd
        ON jd.namespace_id = ns.id
        AND jd.name = b.job_name;

    IF batch_count IS DISTINCT FROM resolved_count THEN
        RAISE EXCEPTION 'ACTA:ENQ_ROUTE_UNKNOWN:Enqueue rejected: one or more rows reference an unknown namespace or job. Has the owning worker run InitializeAsync yet?'
            USING ERRCODE = 'P0001';
    END IF;

    SELECT COUNT(*)
    INTO ns_active_count
    FROM unnest(p_b_namespace_name, p_b_job_name) AS b(namespace_name, job_name)
    INNER JOIN {{schema}}.namespaces ns ON ns.name = b.namespace_name AND ns.status_code = 10 /* JobNamespaceStatusCode.Active */
    INNER JOIN {{schema}}.definitions jd
        ON jd.namespace_id = ns.id
        AND jd.name = b.job_name;

    IF batch_count IS DISTINCT FROM ns_active_count THEN
        RAISE EXCEPTION 'ACTA:ENQ_NS_SUSPENDED:Enqueue rejected: one or more rows reference a suspended namespace.'
            USING ERRCODE = 'P0001';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM unnest(p_b_namespace_name, p_b_job_name) AS b(namespace_name, job_name)
        INNER JOIN {{schema}}.namespaces ns ON ns.name = b.namespace_name
        INNER JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.id AND jd.name = b.job_name
        WHERE jd.status_code <> 10 /* JobDefinitionStatusCode.Active */
    ) THEN
        RAISE EXCEPTION 'ACTA:ENQ_DEF_RETIRED:Enqueue rejected: the job definition is retired.'
            USING ERRCODE = 'P0001';
    END IF;

    SELECT COUNT(*) INTO parent_count
    FROM unnest(p_b_parent_id) AS b(parent_id)
    WHERE b.parent_id IS NOT NULL;

    IF parent_count > 0 THEN
        PERFORM j.id
        FROM {{schema}}.jobs j
        WHERE j.id IN (SELECT DISTINCT b.parent_id FROM unnest(p_b_parent_id) AS b(parent_id) WHERE b.parent_id IS NOT NULL)
        ORDER BY j.id DESC
        FOR UPDATE;
    END IF;

    SELECT COUNT(*) INTO parent_live
    FROM unnest(p_b_parent_id) AS b(parent_id)
    INNER JOIN {{schema}}.jobs pj ON pj.id = b.parent_id
    INNER JOIN {{schema}}.runtimes pr ON pr.job_id = pj.id
    WHERE pr.status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */);

    IF parent_count IS DISTINCT FROM parent_live THEN
        RAISE EXCEPTION 'Enqueue rejected: one or more child rows reference a missing or terminal parent job.'
            USING ERRCODE = 'P0001';
    END IF;

    IF EXISTS (
        SELECT 1 FROM unnest(p_b_tenant_key) AS b(tenant_key)
        WHERE
            b.tenant_key IS NOT NULL
            AND NOT EXISTS (SELECT 1 FROM {{schema}}.tenants t WHERE t.tenant_key = b.tenant_key)
    ) THEN
        RAISE EXCEPTION 'ACTA:ENQ_TENANT_UNKNOWN:Enqueue rejected: one or more rows reference an unknown tenant.'
            USING ERRCODE = 'P0001';
    END IF;

    IF EXISTS (
        SELECT 1 FROM unnest(p_b_tenant_key) AS b(tenant_key)
        JOIN {{schema}}.tenants t ON t.tenant_key = b.tenant_key
        WHERE
            b.tenant_key IS NOT NULL
            AND t.status_code <> 10 /* TenantStatusCode.Active */
    ) THEN
        RAISE EXCEPTION 'ACTA:ENQ_TENANT_SUSPENDED:Enqueue rejected: one or more rows reference a suspended tenant.'
            USING ERRCODE = 'P0001';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM unnest(p_b_namespace_name, p_b_job_name, p_b_tenant_key, p_b_parent_id)
            AS b(namespace_name, job_name, tenant_key, parent_id)
        INNER JOIN {{schema}}.namespaces ns ON ns.name = b.namespace_name
        INNER JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.id AND jd.name = b.job_name
        LEFT JOIN {{schema}}.jobs pj ON pj.id = b.parent_id
        WHERE
            jd.tenant_requirement_code = 10 /* JobTenantRequirementCode.Required */
            AND b.tenant_key IS NULL
            AND pj.tenant_id IS NULL
    ) THEN
        RAISE EXCEPTION 'ACTA:ENQ_TENANT_REQUIRED:Enqueue rejected: one or more rows target a definition that requires a tenant and carry none.'
            USING ERRCODE = 'P0001';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM unnest(p_b_namespace_name, p_b_job_name, p_b_tenant_key) AS b(namespace_name, job_name, tenant_key)
        INNER JOIN {{schema}}.namespaces ns ON ns.name = b.namespace_name
        INNER JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.id AND jd.name = b.job_name
        WHERE
            jd.tenant_requirement_code = 20 /* JobTenantRequirementCode.Forbidden */
            AND b.tenant_key IS NOT NULL
    ) THEN
        RAISE EXCEPTION 'ACTA:ENQ_TENANT_FORBIDDEN:Enqueue rejected: one or more rows target a definition that forbids a tenant and name one.'
            USING ERRCODE = 'P0001';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM unnest(p_b_tenant_key, p_b_parent_id, p_b_tenant_override) AS b(tenant_key, parent_id, tenant_override)
        INNER JOIN {{schema}}.tenants t ON t.tenant_key = b.tenant_key
        INNER JOIN {{schema}}.jobs pj ON pj.id = b.parent_id
        WHERE
            NOT b.tenant_override
            AND pj.tenant_id IS NOT NULL
            AND pj.tenant_id <> t.id
    ) THEN
        RAISE EXCEPTION 'ACTA:ENQ_TENANT_MISMATCH:Enqueue rejected: one or more child rows name a TenantKey that differs from the parent tenant without an explicit override.'
            USING ERRCODE = 'P0001';
    END IF;

    -- Resolve catalog + pre-allocate the job id ONCE into a temp table keyed by that id, so the inserts
    -- below read a staged, ANALYZEd set instead of re-joining a catalog CTE the planner sizes at ~1 row
    -- (which forced batch-rescanning Nested Loops after runtime state split into its own table).
    CREATE TEMP TABLE IF NOT EXISTS _enq_batch (
        id BIGINT PRIMARY KEY,
        ordinal INT NOT NULL,
        job_ref UUID NOT NULL,
        parent_id BIGINT,
        lineage_root_id BIGINT,
        deduplication_key VARCHAR,
        correlation_key VARCHAR,
        namespace_id SMALLINT NOT NULL,
        definition_id INT NOT NULL,
        tenant_id INT,
        input_format_id SMALLINT NOT NULL,
        input BYTEA,
        exclusive_key VARCHAR,
        audit_level_code SMALLINT NOT NULL,
        priority_code SMALLINT NOT NULL,
        next_run_at_utc TIMESTAMPTZ NOT NULL,
        is_child BOOLEAN NOT NULL
    ) ON COMMIT DROP;
    TRUNCATE _enq_batch;

    INSERT INTO _enq_batch (
        id,
        ordinal,
        job_ref,
        parent_id,
        lineage_root_id,
        deduplication_key,
        correlation_key,
        namespace_id,
        definition_id,
        tenant_id,
        input_format_id,
        input,
        exclusive_key,
        audit_level_code,
        priority_code,
        next_run_at_utc,
        is_child)
    SELECT
        nextval(pg_get_serial_sequence('{{schema}}.jobs', 'id')),
        b.ordinal,
        b.job_ref,
        b.parent_id,
        CASE WHEN b.parent_id IS NOT NULL THEN COALESCE(pj.lineage_root_id, pj.id) END,
        b.deduplication_key,
        COALESCE(b.correlation_key, pj.correlation_key),
        ns.id,
        jd.id,
        CASE WHEN jd.tenant_requirement_code = 20 /* JobTenantRequirementCode.Forbidden */ THEN NULL
            ELSE COALESCE(t.id, pj.tenant_id) END,
        b.input_format_id,
        b.input,
        b.exclusive_key,
        jd.audit_level_code_effective,
        COALESCE(b.priority_override, jd.priority_code_effective),
        COALESCE(b.next_run_at_utc, now() + make_interval(secs => COALESCE(b.delay_seconds, 0))),
        (b.parent_id IS NOT NULL)
    FROM unnest(
        p_b_ordinal, p_b_job_ref, p_b_namespace_name, p_b_job_name,
        p_b_deduplication_key, p_b_correlation_key, p_b_priority_override,
        p_b_input_format_id, p_b_input, p_b_exclusive_key, p_b_next_run_at_utc,
        p_b_delay_seconds, p_b_parent_id, p_b_tenant_key
    ) AS b(ordinal, job_ref, namespace_name, job_name,
        deduplication_key, correlation_key, priority_override,
        input_format_id, input, exclusive_key, next_run_at_utc,
        delay_seconds, parent_id, tenant_key)
    INNER JOIN {{schema}}.namespaces ns ON ns.name = b.namespace_name AND ns.status_code = 10 /* JobNamespaceStatusCode.Active */
    INNER JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.id AND jd.name = b.job_name
    LEFT JOIN {{schema}}.tenants t ON t.tenant_key = b.tenant_key AND t.status_code = 10 /* TenantStatusCode.Active */
    LEFT JOIN {{schema}}.jobs pj ON pj.id = b.parent_id;

    -- Give the planner real row counts for the staged set so the inserts below stay hash/index joins.
    ANALYZE _enq_batch;

    RETURN QUERY
    WITH inserted_root AS (
        INSERT INTO {{schema}}.jobs (
            id,
            job_ref,
            lineage_root_id,
            parent_id,
            deduplication_key,
            correlation_key,
            namespace_id,
            definition_id,
            tenant_id,
            input_format_id,
            input,
            exclusive_key,
            audit_level_code,
            created_at_utc)
        OVERRIDING SYSTEM VALUE
        SELECT
            e.id,
            e.job_ref,
            NULL,
            NULL,
            e.deduplication_key,
            e.correlation_key,
            e.namespace_id,
            e.definition_id,
            e.tenant_id,
            e.input_format_id,
            e.input,
            e.exclusive_key,
            e.audit_level_code,
            now()
        FROM _enq_batch e
        WHERE NOT e.is_child
        ON CONFLICT (namespace_id, deduplication_key)
            WHERE deduplication_key IS NOT NULL AND parent_id IS NULL
            DO NOTHING
        RETURNING id
    ),
    inserted_child AS (
        INSERT INTO {{schema}}.jobs (
            id,
            job_ref,
            lineage_root_id,
            parent_id,
            deduplication_key,
            correlation_key,
            namespace_id,
            definition_id,
            tenant_id,
            input_format_id,
            input,
            exclusive_key,
            audit_level_code,
            created_at_utc)
        OVERRIDING SYSTEM VALUE
        SELECT
            e.id,
            e.job_ref,
            e.lineage_root_id,
            e.parent_id,
            e.deduplication_key,
            e.correlation_key,
            e.namespace_id,
            e.definition_id,
            e.tenant_id,
            e.input_format_id,
            e.input,
            e.exclusive_key,
            e.audit_level_code,
            now()
        FROM _enq_batch e
        WHERE e.is_child
        ON CONFLICT (parent_id, deduplication_key)
            WHERE deduplication_key IS NOT NULL AND parent_id IS NOT NULL
            DO NOTHING
        RETURNING id
    ),
    inserted AS (
        SELECT id FROM inserted_root
        UNION ALL
        SELECT id FROM inserted_child
    ),
    runtime_insert AS (
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
            version)
        SELECT
            e.id,
            e.namespace_id,
            10 /* JobStatusCode.Ready */,
            e.priority_code,
            e.next_run_at_utc,
            0,
            0,
            NULL,
            now(),
            0
        FROM inserted i
        INNER JOIN _enq_batch e ON e.id = i.id
        RETURNING 1
    ),
    tag_insert AS (
        INSERT INTO {{schema}}.tags (scope_code, scope_id, namespace_id, name, value, value_search)
        SELECT 50 /* TagScopeCode.Job */, e.id, e.namespace_id, t.name, t.value, t.value_search
        FROM unnest(p_t_ordinal, p_t_name, p_t_value, p_t_value_search) AS t(ordinal, name, value, value_search)
        INNER JOIN _enq_batch e ON e.ordinal = t.ordinal
        INNER JOIN inserted i ON i.id = e.id
        RETURNING 1
    ),
    existing AS (
        SELECT e.ordinal, j.id, j.job_ref
        FROM _enq_batch e
        INNER JOIN {{schema}}.jobs j
            ON (NOT e.is_child
                AND j.namespace_id = e.namespace_id
                AND j.deduplication_key = e.deduplication_key
                AND j.parent_id IS NULL)
            OR (e.is_child
                AND j.parent_id = e.parent_id
                AND j.deduplication_key = e.deduplication_key)
        WHERE
            e.deduplication_key IS NOT NULL
            AND NOT EXISTS (
                SELECT 1
                FROM inserted i
                WHERE i.id = e.id)
    )
    SELECT
        e.ordinal,
        COALESCE(i.id, ex.id) AS job_id,
        CASE WHEN i.id IS NOT NULL THEN e.job_ref ELSE ex.job_ref END AS job_ref,
        CASE WHEN i.id IS NOT NULL
            THEN 1 /* JobEnqueueAction.Inserted */
            ELSE 2 /* JobEnqueueAction.Deduplicated */ END AS action
    FROM _enq_batch e
    LEFT JOIN inserted i ON i.id = e.id
    LEFT JOIN existing ex ON ex.ordinal = e.ordinal
    ORDER BY e.ordinal;
END;
$$;

-- CREATE OR REPLACE across arities creates an overload instead of replacing; drop the retired
-- signature (without p_b_tenant_override) so pre-existing installs cannot resolve the stale form.
DROP FUNCTION IF EXISTS {{schema}}.enqueue_batch(
    INT [], UUID [], VARCHAR [], VARCHAR [], VARCHAR [], VARCHAR [], SMALLINT [], SMALLINT [], BYTEA [],
    VARCHAR [], TIMESTAMPTZ [], INT [], BIGINT [], VARCHAR [], INT [], VARCHAR [], VARCHAR [], VARCHAR []
);
