-- Lock order: the batch's lanes in id order, then the parent rows (docs/sql-internals.md,
-- "Lane lock order").
CREATE OR REPLACE FUNCTION {{schema}}.enqueue_jobs(
    p_b_ordinal INT [],
    p_b_job_ref UUID [],
    p_b_namespace_name VARCHAR [],
    p_b_job_name VARCHAR [],
    p_b_deduplication_key VARCHAR [],
    p_b_correlation_key VARCHAR [],
    p_b_job_priority_code_override SMALLINT [],
    p_b_input_format_id SMALLINT [],
    p_b_input BYTEA [],
    p_b_concurrency_key VARCHAR [],
    p_b_next_run_at_utc TIMESTAMPTZ [],
    p_b_delay_seconds INT [],
    p_b_parent_job_id BIGINT [],
    p_b_tenant_name VARCHAR [],
    p_b_tenant_override BOOLEAN [],
    p_tag_ordinal INT [],
    p_tag_name VARCHAR [],
    p_tag_value VARCHAR [],
    p_tag_value_search VARCHAR [],
    p_b_lane_name VARCHAR [] DEFAULT NULL
)
RETURNS TABLE (ordinal INT, job_id BIGINT, job_ref UUID, action INT)
LANGUAGE plpgsql
AS $$
DECLARE
    batch_count INT;
    inserted_count INT;
    resolved_count INT;
    laned_count INT;
    ns_active_count INT;
    parent_count INT;
    parent_live INT;
    lane_count INT;
    locked_count INT;
    locked_lane RECORD;
    v_lane VARCHAR [];
BEGIN

    batch_count := COALESCE(array_length(p_b_ordinal, 1), 0);

    -- Each row's effective lane is its own, else its definition's, read once: a definition registered
    -- mid-batch cannot move a row out of the lane locked for it.
    SELECT
        COUNT(*),
        COUNT(*) FILTER (WHERE COALESCE(b.lane, jd.lane_name) IS NOT NULL),
        array_agg(COALESCE(b.lane, jd.lane_name) ORDER BY b.ord)
    INTO resolved_count, laned_count, v_lane
    FROM unnest(p_b_namespace_name, p_b_job_name, p_b_lane_name) WITH ORDINALITY AS b(namespace_name, job_name, lane, ord)
    INNER JOIN {{schema}}.namespaces ns ON ns.namespace_name = b.namespace_name
    INNER JOIN {{schema}}.definitions jd
        ON jd.namespace_id = ns.namespace_id
        AND jd.definition_name = b.job_name;

    IF batch_count IS DISTINCT FROM resolved_count THEN
        RAISE EXCEPTION 'ACTA:ENQ_ROUTE_UNKNOWN:Enqueue rejected: one or more rows reference an unknown namespace or job. Has the owning worker run InitializeAsync yet?'
            USING ERRCODE = 'P0001';
    END IF;

    SELECT COUNT(*)
    INTO ns_active_count
    FROM unnest(p_b_namespace_name, p_b_job_name) AS b(namespace_name, job_name)
    INNER JOIN {{schema}}.namespaces ns
        ON ns.namespace_name = b.namespace_name
        AND ns.namespace_status_code = 10 /* NamespaceStatusCode.Active */
    INNER JOIN {{schema}}.definitions jd
        ON jd.namespace_id = ns.namespace_id
        AND jd.definition_name = b.job_name;

    IF batch_count IS DISTINCT FROM ns_active_count THEN
        RAISE EXCEPTION 'ACTA:ENQ_NS_SUSPENDED:Enqueue rejected: one or more rows reference a suspended namespace.'
            USING ERRCODE = 'P0001';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM unnest(p_b_namespace_name, p_b_job_name) AS b(namespace_name, job_name)
        INNER JOIN {{schema}}.namespaces ns ON ns.namespace_name = b.namespace_name
        INNER JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.namespace_id AND jd.definition_name = b.job_name
        WHERE jd.definition_status_code <> 10 /* DefinitionStatusCode.Active */
    ) THEN
        RAISE EXCEPTION 'ACTA:ENQ_DEF_RETIRED:Enqueue rejected: the job definition is retired.'
            USING ERRCODE = 'P0001';
    END IF;

    -- Missing lanes are inserted in name order, then the whole set is locked in id order
    -- (docs/sql-internals.md, "Lane lock order"). An unlaned batch skips all of it, the
    -- lane table's per-call temp-table create included.
    lane_count := 0;
    IF laned_count > 0 THEN
        CREATE TEMP TABLE IF NOT EXISTS _enq_lanes (
            namespace_id INT NOT NULL,
            lane_name VARCHAR NOT NULL,
            lane_id BIGINT,
            busy BOOLEAN,
            PRIMARY KEY (namespace_id, lane_name)
        ) ON COMMIT DROP;
        TRUNCATE _enq_lanes;

        INSERT INTO _enq_lanes (namespace_id, lane_name)
        SELECT DISTINCT ns.namespace_id, b.lane
        FROM unnest(p_b_namespace_name, v_lane) AS b(namespace_name, lane)
        INNER JOIN {{schema}}.namespaces ns ON ns.namespace_name = b.namespace_name
        WHERE b.lane IS NOT NULL;

        SELECT COUNT(*) INTO lane_count FROM _enq_lanes;

        -- Ready versus Blocked is read after the lane locks, which only a per-statement snapshot sees.
        IF current_setting('transaction_isolation') <> 'read committed' THEN
            RAISE EXCEPTION 'ACTA:ENQ_LANE_ISOLATION:Enqueue rejected: a laned enqueue needs a READ COMMITTED caller transaction.'
                USING ERRCODE = 'P0001';
        END IF;

        LOOP
            INSERT INTO {{schema}}.lanes (namespace_id, lane_name, created_at_utc)
            SELECT e.namespace_id, e.lane_name, now()
            FROM _enq_lanes e
            WHERE NOT EXISTS (
                SELECT 1
                FROM {{schema}}.lanes l
                WHERE l.namespace_id = e.namespace_id AND l.lane_name = e.lane_name
            )
            ORDER BY e.lane_name
            ON CONFLICT (namespace_id, lane_name) DO NOTHING;

            -- Only a row this loop locked records its id, so a lane that another transaction creates
            -- after the lock statement cannot pass for a locked one.
            UPDATE _enq_lanes SET lane_id = NULL WHERE lane_id IS NOT NULL;
            FOR locked_lane IN
                SELECT l.lane_id, l.namespace_id, l.lane_name
                FROM {{schema}}.lanes l
                INNER JOIN _enq_lanes e ON e.namespace_id = l.namespace_id AND e.lane_name = l.lane_name
                ORDER BY l.lane_id
                FOR UPDATE OF l
            LOOP
                UPDATE _enq_lanes e
                SET lane_id = locked_lane.lane_id
                WHERE e.namespace_id = locked_lane.namespace_id AND e.lane_name = locked_lane.lane_name;
            END LOOP;

            SELECT COUNT(*) INTO locked_count FROM _enq_lanes e WHERE e.lane_id IS NOT NULL;
            EXIT WHEN locked_count = lane_count;
        END LOOP;

        -- Under the lane locks, one probe per lane records whether an unfinished member is already there.
        UPDATE _enq_lanes e
        SET busy = EXISTS (
            SELECT 1
            FROM {{schema}}.jobs_state m
            WHERE
                m.lane_id = e.lane_id
                AND m.lane_id IS NOT NULL
                AND m.job_status_code IN (
                    10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                    30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                )
        )
        WHERE e.lane_id IS NOT NULL;
    END IF;

    SELECT COUNT(*) INTO parent_count
    FROM unnest(p_b_parent_job_id) AS b(parent_job_id)
    WHERE b.parent_job_id IS NOT NULL;

    IF parent_count > 0 THEN
        PERFORM j.job_id
        FROM {{schema}}.jobs j
        WHERE j.job_id IN (
            SELECT DISTINCT b.parent_job_id FROM unnest(p_b_parent_job_id) AS b(parent_job_id) WHERE b.parent_job_id IS NOT NULL
        )
        ORDER BY j.job_id DESC
        FOR UPDATE;
    END IF;

    SELECT COUNT(*) INTO parent_live
    FROM unnest(p_b_parent_job_id) AS b(parent_job_id)
    INNER JOIN {{schema}}.jobs pj ON pj.job_id = b.parent_job_id
    INNER JOIN {{schema}}.jobs_state pr ON pr.job_id = pj.job_id
    WHERE pr.job_status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */);

    IF parent_count IS DISTINCT FROM parent_live THEN
        RAISE EXCEPTION 'Enqueue rejected: one or more child rows reference a missing or terminal parent job.'
            USING ERRCODE = 'P0001';
    END IF;

    IF EXISTS (
        SELECT 1 FROM unnest(p_b_tenant_name) AS b(tenant_name)
        WHERE
            b.tenant_name IS NOT NULL
            AND NOT EXISTS (SELECT 1 FROM {{schema}}.tenants t WHERE t.tenant_name = b.tenant_name)
    ) THEN
        RAISE EXCEPTION 'ACTA:ENQ_TENANT_UNKNOWN:Enqueue rejected: one or more rows reference an unknown tenant.'
            USING ERRCODE = 'P0001';
    END IF;

    IF EXISTS (
        SELECT 1 FROM unnest(p_b_tenant_name) AS b(tenant_name)
        JOIN {{schema}}.tenants t ON t.tenant_name = b.tenant_name
        WHERE
            b.tenant_name IS NOT NULL
            AND t.tenant_status_code <> 10 /* TenantStatusCode.Active */
    ) THEN
        RAISE EXCEPTION 'ACTA:ENQ_TENANT_SUSPENDED:Enqueue rejected: one or more rows reference a suspended tenant.'
            USING ERRCODE = 'P0001';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM unnest(p_b_namespace_name, p_b_job_name, p_b_tenant_name, p_b_parent_job_id)
            AS b(namespace_name, job_name, tenant_name, parent_job_id)
        INNER JOIN {{schema}}.namespaces ns ON ns.namespace_name = b.namespace_name
        INNER JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.namespace_id AND jd.definition_name = b.job_name
        LEFT JOIN {{schema}}.jobs pj ON pj.job_id = b.parent_job_id
        WHERE
            jd.tenant_requirement_code = 10 /* DefinitionTenantRequirementCode.Required */
            AND b.tenant_name IS NULL
            AND pj.tenant_id IS NULL
    ) THEN
        RAISE EXCEPTION 'ACTA:ENQ_TENANT_REQUIRED:Enqueue rejected: one or more rows target a definition that requires a tenant and carry none.'
            USING ERRCODE = 'P0001';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM unnest(p_b_namespace_name, p_b_job_name, p_b_tenant_name) AS b(namespace_name, job_name, tenant_name)
        INNER JOIN {{schema}}.namespaces ns ON ns.namespace_name = b.namespace_name
        INNER JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.namespace_id AND jd.definition_name = b.job_name
        WHERE
            jd.tenant_requirement_code = 20 /* DefinitionTenantRequirementCode.Forbidden */
            AND b.tenant_name IS NOT NULL
    ) THEN
        RAISE EXCEPTION 'ACTA:ENQ_TENANT_FORBIDDEN:Enqueue rejected: one or more rows target a definition that forbids a tenant and name one.'
            USING ERRCODE = 'P0001';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM unnest(p_b_tenant_name, p_b_parent_job_id, p_b_tenant_override) AS b(tenant_name, parent_job_id, tenant_override)
        INNER JOIN {{schema}}.tenants t ON t.tenant_name = b.tenant_name
        INNER JOIN {{schema}}.jobs pj ON pj.job_id = b.parent_job_id
        WHERE
            NOT b.tenant_override
            AND pj.tenant_id IS NOT NULL
            AND pj.tenant_id <> t.tenant_id
    ) THEN
        RAISE EXCEPTION 'ACTA:ENQ_TENANT_MISMATCH:Enqueue rejected: one or more child rows name a TenantName that differs from the parent tenant without an explicit override.'
            USING ERRCODE = 'P0001';
    END IF;

    -- Resolve catalog + pre-allocate the job id ONCE into a temp table keyed by that id, so the inserts
    -- below read a staged, ANALYZEd set instead of re-joining a catalog CTE the planner sizes at ~1 row
    -- (which forces batch-rescanning Nested Loops once the job state sits in its own table).
    CREATE TEMP TABLE IF NOT EXISTS _enq_batch (
        job_id BIGINT PRIMARY KEY,
        ordinal INT NOT NULL,
        job_ref UUID NOT NULL,
        parent_job_id BIGINT,
        lineage_root_job_id BIGINT,
        deduplication_key VARCHAR,
        correlation_key VARCHAR,
        namespace_id INT NOT NULL,
        definition_id INT NOT NULL,
        tenant_id INT,
        input_format_id SMALLINT NOT NULL,
        input BYTEA,
        concurrency_key VARCHAR,
        event_recording_code SMALLINT NOT NULL,
        job_priority_code SMALLINT NOT NULL,
        next_run_at_utc TIMESTAMPTZ NOT NULL,
        is_child BOOLEAN NOT NULL,
        lane VARCHAR,
        lane_id BIGINT,
        lane_busy BOOLEAN
    ) ON COMMIT DROP;
    TRUNCATE _enq_batch;

    -- Ids are drawn first and handed out by rank, so job-id order equals ordinal order however the
    -- sequence calls interleave: a lane runs in job-id order, which must be the caller's batch order.
    WITH drawn AS (
        SELECT nextval(pg_get_serial_sequence('{{schema}}.jobs', 'job_id')) AS job_id
        FROM generate_series(1, batch_count)
    ),
    ranked_ids AS (
        SELECT d.job_id, row_number() OVER (ORDER BY d.job_id) AS rn
        FROM drawn d
    ),
    batch_rows AS (
        SELECT
            b.*,
            row_number() OVER (ORDER BY b.ordinal) AS rn
        FROM unnest(
            p_b_ordinal, p_b_job_ref, p_b_namespace_name, p_b_job_name,
            p_b_deduplication_key, p_b_correlation_key, p_b_job_priority_code_override,
            p_b_input_format_id, p_b_input, p_b_concurrency_key, p_b_next_run_at_utc,
            p_b_delay_seconds, p_b_parent_job_id, p_b_tenant_name, v_lane
        ) AS b(ordinal, job_ref, namespace_name, job_name,
            deduplication_key, correlation_key, priority_override,
            input_format_id, input, concurrency_key, next_run_at_utc,
            delay_seconds, parent_job_id, tenant_name, lane)
    )
    INSERT INTO _enq_batch (
        job_id,
        ordinal,
        job_ref,
        parent_job_id,
        lineage_root_job_id,
        deduplication_key,
        correlation_key,
        namespace_id,
        definition_id,
        tenant_id,
        input_format_id,
        input,
        concurrency_key,
        event_recording_code,
        job_priority_code,
        next_run_at_utc,
        is_child,
        lane)
    SELECT
        ri.job_id,
        b.ordinal,
        b.job_ref,
        b.parent_job_id,
        CASE WHEN b.parent_job_id IS NOT NULL THEN COALESCE(pj.lineage_root_job_id, pj.job_id) END,
        b.deduplication_key,
        COALESCE(b.correlation_key, pj.correlation_key),
        ns.namespace_id,
        jd.definition_id,
        CASE WHEN jd.tenant_requirement_code = 20 /* DefinitionTenantRequirementCode.Forbidden */ THEN NULL
            ELSE COALESCE(t.tenant_id, pj.tenant_id) END,
        b.input_format_id,
        b.input,
        b.concurrency_key,
        jd.event_recording_code_effective,
        COALESCE(b.priority_override, jd.job_priority_code_effective),
        COALESCE(b.next_run_at_utc, now() + make_interval(secs => COALESCE(b.delay_seconds, 0))),
        (b.parent_job_id IS NOT NULL),
        b.lane
    FROM batch_rows b
    INNER JOIN ranked_ids ri ON ri.rn = b.rn
    INNER JOIN {{schema}}.namespaces ns
        ON ns.namespace_name = b.namespace_name
        AND ns.namespace_status_code = 10 /* NamespaceStatusCode.Active */
    INNER JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.namespace_id AND jd.definition_name = b.job_name
    LEFT JOIN {{schema}}.tenants t ON t.tenant_name = b.tenant_name AND t.tenant_status_code = 10 /* TenantStatusCode.Active */
    LEFT JOIN {{schema}}.jobs pj ON pj.job_id = b.parent_job_id;

    IF lane_count > 0 THEN
        UPDATE _enq_batch e
        SET lane_id = el.lane_id, lane_busy = el.busy
        FROM _enq_lanes el
        WHERE el.namespace_id = e.namespace_id AND el.lane_name = e.lane;

        -- A child may not wait behind an unfinished ancestor in its own lane: the ancestor waits for it.
        IF parent_count > 0 THEN
            IF EXISTS (
                WITH RECURSIVE ancestors AS (
                    SELECT e.lane_id, a.job_id, a.parent_job_id
                    FROM _enq_batch e
                    INNER JOIN {{schema}}.jobs a ON a.job_id = e.parent_job_id
                    WHERE e.lane_id IS NOT NULL
                    UNION ALL
                    SELECT c.lane_id, a.job_id, a.parent_job_id
                    FROM {{schema}}.jobs a
                    INNER JOIN ancestors c ON a.job_id = c.parent_job_id
                )
                SELECT 1
                FROM ancestors c
                INNER JOIN {{schema}}.jobs_state ar ON ar.job_id = c.job_id
                WHERE
                    ar.lane_id = c.lane_id
                    AND ar.job_status_code IN (
                        10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                        30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                    )
            ) THEN
                RAISE EXCEPTION 'ACTA:ENQ_ANCESTOR_LANE:Enqueue rejected: one or more child rows name the lane of an unfinished ancestor, so they would wait behind the ancestor that waits for them.'
                    USING ERRCODE = 'P0001';
            END IF;
        END IF;
    END IF;

    -- Give the planner real row counts for the staged set so the inserts below stay hash/index joins.
    ANALYZE _enq_batch;

    -- The inserts run as one statement and the outcomes are read by the next: a row skipped because a
    -- concurrent producer committed its key while this one waited is invisible to this statement's snapshot,
    -- and only a later statement's sees it to return its id (the register_tenant pattern).
    WITH inserted_root AS (
        INSERT INTO {{schema}}.jobs (
            job_kind_code,
            job_id,
            job_ref,
            lineage_root_job_id,
            parent_job_id,
            deduplication_key,
            correlation_key,
            namespace_id,
            definition_id,
            tenant_id,
            input_format_id,
            input,
            concurrency_key,
            event_recording_code,
            created_at_utc)
        OVERRIDING SYSTEM VALUE
        SELECT
            10 /* JobKindCode.Ordinary */,
            e.job_id,
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
            e.concurrency_key,
            e.event_recording_code,
            now()
        FROM _enq_batch e
        WHERE NOT e.is_child
        ON CONFLICT (namespace_id, deduplication_key)
            WHERE deduplication_key IS NOT NULL AND parent_job_id IS NULL
            DO NOTHING
        RETURNING jobs.job_id
    ),
    inserted_child AS (
        INSERT INTO {{schema}}.jobs (
            job_kind_code,
            job_id,
            job_ref,
            lineage_root_job_id,
            parent_job_id,
            deduplication_key,
            correlation_key,
            namespace_id,
            definition_id,
            tenant_id,
            input_format_id,
            input,
            concurrency_key,
            event_recording_code,
            created_at_utc)
        OVERRIDING SYSTEM VALUE
        SELECT
            10 /* JobKindCode.Ordinary */,
            e.job_id,
            e.job_ref,
            e.lineage_root_job_id,
            e.parent_job_id,
            e.deduplication_key,
            e.correlation_key,
            e.namespace_id,
            e.definition_id,
            e.tenant_id,
            e.input_format_id,
            e.input,
            e.concurrency_key,
            e.event_recording_code,
            now()
        FROM _enq_batch e
        WHERE e.is_child
        ON CONFLICT (parent_job_id, deduplication_key)
            WHERE deduplication_key IS NOT NULL AND parent_job_id IS NOT NULL
            DO NOTHING
        RETURNING jobs.job_id
    ),
    inserted AS (
        SELECT r.job_id FROM inserted_root r
        UNION ALL
        SELECT c.job_id FROM inserted_child c
    ),
    -- A laned row enters Ready only as the first inserted row of its lane with no unfinished member
    -- already there; every later one waits as Blocked. The window ranks inserted rows only, because a
    -- deduplicated row is decided in this statement and lands no jobs_state row.
    state_insert AS (
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
            version)
        SELECT
            e.job_id,
            e.namespace_id,
            e.lane_id,
            CASE
                WHEN e.lane_id IS NOT NULL AND (
                    row_number() OVER (PARTITION BY e.lane_id ORDER BY e.job_id) > 1
                    OR e.lane_busy
                )
                    THEN 15 /* JobStatusCode.Blocked */
                ELSE 10 /* JobStatusCode.Ready */
            END,
            e.job_priority_code,
            e.next_run_at_utc,
            0,
            0,
            NULL,
            now(),
            0
        FROM inserted i
        INNER JOIN _enq_batch e ON e.job_id = i.job_id
        RETURNING 1
    ),
    tag_insert AS (
        INSERT INTO {{schema}}.tags (entity_code, entity_id, tag_name, tag_value, tag_value_search)
        SELECT 50 /* EntityCode.Job */, e.job_id, t.tag_name, t.tag_value, t.tag_value_search
        FROM unnest(p_tag_ordinal, p_tag_name, p_tag_value, p_tag_value_search) AS t(ordinal, tag_name, tag_value, tag_value_search)
        INNER JOIN _enq_batch e ON e.ordinal = t.ordinal
        INNER JOIN inserted i ON i.job_id = e.job_id
        RETURNING 1
    )
    SELECT count(*) INTO inserted_count FROM inserted;

    RETURN QUERY
    SELECT
        e.ordinal,
        COALESCE(mine.job_id, root.job_id, child.job_id) AS job_id,
        COALESCE(mine.job_ref, root.job_ref, child.job_ref) AS job_ref,
        CASE WHEN mine.job_id IS NOT NULL
            THEN 1 /* EnqueueJobAction.Inserted */
            ELSE 2 /* EnqueueJobAction.Deduplicated */ END AS action
    FROM _enq_batch e
    LEFT JOIN {{schema}}.jobs mine ON mine.job_id = e.job_id
    LEFT JOIN {{schema}}.jobs root
        ON mine.job_id IS NULL
        AND NOT e.is_child
        AND root.namespace_id = e.namespace_id
        AND root.deduplication_key = e.deduplication_key
        AND root.parent_job_id IS NULL
    LEFT JOIN {{schema}}.jobs child
        ON mine.job_id IS NULL
        AND e.is_child
        AND child.parent_job_id = e.parent_job_id
        AND child.deduplication_key = e.deduplication_key
    ORDER BY e.ordinal;
END;
$$;