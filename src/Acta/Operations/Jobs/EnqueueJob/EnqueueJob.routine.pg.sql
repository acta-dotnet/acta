-- Only p_namespace_name and p_job_name are required; other params default (p_job_ref is server-generated
-- when omitted; p_input_format_id defaults json/none by input presence). Parameter ORDER is fixed:
-- the provider store invokes this function positionally.
CREATE OR REPLACE FUNCTION {{schema}}.enqueue_job(
    p_job_ref UUID DEFAULT GEN_RANDOM_UUID(),
    p_namespace_name VARCHAR DEFAULT NULL,
    p_job_name VARCHAR DEFAULT NULL,
    p_deduplication_key VARCHAR DEFAULT NULL,
    p_correlation_key VARCHAR DEFAULT NULL,
    p_job_priority_code_override SMALLINT DEFAULT NULL,
    p_input_format_id SMALLINT DEFAULT NULL,
    p_input BYTEA DEFAULT NULL,
    p_concurrency_key VARCHAR DEFAULT NULL,
    p_next_run_at_utc TIMESTAMPTZ DEFAULT NULL,
    p_delay_seconds INT DEFAULT NULL,
    p_parent_job_id BIGINT DEFAULT NULL,
    p_tenant_name VARCHAR DEFAULT NULL,
    p_tenant_override BOOLEAN DEFAULT FALSE,
    p_tag_name VARCHAR [] DEFAULT NULL,
    p_tag_value VARCHAR [] DEFAULT NULL,
    p_tag_value_search VARCHAR [] DEFAULT NULL,
    p_lane_name VARCHAR DEFAULT NULL,
    p_parent_execution_number INT DEFAULT NULL,
    p_parent_worker_id INT DEFAULT NULL
)
RETURNS TABLE (ordinal INT, job_id BIGINT, job_ref UUID, action INT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_ns_id INT;
    v_ns_status SMALLINT;
    v_def_id INT;
    v_def_priority SMALLINT;
    v_def_event_recording SMALLINT;
    v_def_status SMALLINT;
    v_def_tenant_req SMALLINT;
    v_tenant_id INT;
    v_tenant_status SMALLINT;
    v_lineage BIGINT;
    v_parent_corr VARCHAR;
    v_parent_tenant INT;
    v_job_id BIGINT;
    v_lane VARCHAR;
    v_lane_id BIGINT;
BEGIN
    SELECT
        ns.namespace_id,
        ns.namespace_status_code,
        jd.definition_id,
        jd.job_priority_code_effective,
        jd.event_recording_code_effective,
        jd.definition_status_code,
        jd.tenant_requirement_code,
        COALESCE(p_lane_name, jd.lane_name)
    INTO v_ns_id, v_ns_status, v_def_id, v_def_priority, v_def_event_recording, v_def_status, v_def_tenant_req, v_lane
    FROM {{schema}}.namespaces ns
    INNER JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.namespace_id AND jd.definition_name = p_job_name
    WHERE ns.namespace_name = p_namespace_name;

    IF v_def_id IS NULL THEN
        RAISE EXCEPTION 'ACTA:ENQ_ROUTE_UNKNOWN:Enqueue rejected: one or more rows reference an unknown namespace or job. Has the owning worker run InitializeAsync yet?'
            USING ERRCODE = 'P0001';
    END IF;

    IF v_ns_status <> 10 /* NamespaceStatusCode.Active */ THEN
        RAISE EXCEPTION 'ACTA:ENQ_NS_SUSPENDED:Enqueue rejected: one or more rows reference a suspended namespace.'
            USING ERRCODE = 'P0001';
    END IF;

    IF v_def_status <> 10 /* DefinitionStatusCode.Active */ THEN
        RAISE EXCEPTION 'ACTA:ENQ_DEF_RETIRED:Enqueue rejected: the job definition is retired.'
            USING ERRCODE = 'P0001';
    END IF;

    IF p_tenant_name IS NOT NULL THEN
        SELECT t.tenant_id, t.tenant_status_code INTO v_tenant_id, v_tenant_status
        FROM {{schema}}.tenants t
        WHERE t.tenant_name = p_tenant_name;

        IF v_tenant_id IS NULL THEN
            RAISE EXCEPTION 'ACTA:ENQ_TENANT_UNKNOWN:Enqueue rejected: one or more rows reference an unknown tenant.'
                USING ERRCODE = 'P0001';
        END IF;

        IF v_tenant_status <> 10 /* TenantStatusCode.Active */ THEN
            RAISE EXCEPTION 'ACTA:ENQ_TENANT_SUSPENDED:Enqueue rejected: one or more rows reference a suspended tenant.'
                USING ERRCODE = 'P0001';
        END IF;
    END IF;

    IF v_def_tenant_req = 20 /* DefinitionTenantRequirementCode.Forbidden */ AND p_tenant_name IS NOT NULL THEN
        RAISE EXCEPTION 'ACTA:ENQ_TENANT_FORBIDDEN:Enqueue rejected: the job definition forbids a tenant and the row names one.'
            USING ERRCODE = 'P0001';
    END IF;

    -- The lane row is the lane's mutex: held to commit, it serializes this enqueue against every settle
    -- and enqueue in the lane, so Ready versus Blocked below is decided on a stable member set. A lane
    -- that retention deletes before the lock finds no row here, and the loop inserts it again.
    IF v_lane IS NOT NULL THEN
        -- Ready versus Blocked is read after the lane lock, which only a per-statement snapshot sees.
        IF current_setting('transaction_isolation') <> 'read committed' THEN
            RAISE EXCEPTION 'ACTA:ENQ_LANE_ISOLATION:Enqueue rejected: a laned enqueue needs a READ COMMITTED caller transaction.'
                USING ERRCODE = 'P0001';
        END IF;

        LOOP
            SELECT l.lane_id INTO v_lane_id
            FROM {{schema}}.lanes l
            WHERE l.namespace_id = v_ns_id AND l.lane_name = v_lane
            FOR UPDATE;
            EXIT WHEN v_lane_id IS NOT NULL;

            INSERT INTO {{schema}}.lanes (namespace_id, lane_name, created_at_utc)
            VALUES (v_ns_id, v_lane, now())
            ON CONFLICT (namespace_id, lane_name) DO NOTHING;
        END LOOP;
    END IF;

    IF p_parent_job_id IS NOT NULL THEN
        SELECT COALESCE(pj.lineage_root_job_id, pj.job_id), pj.correlation_key, pj.tenant_id
        INTO v_lineage, v_parent_corr, v_parent_tenant
        FROM {{schema}}.jobs pj
        INNER JOIN {{schema}}.jobs_state pr ON pr.job_id = pj.job_id
        WHERE
            pj.job_id = p_parent_job_id
            AND pr.job_status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
        FOR UPDATE OF pj;

        -- A child its parent's handler starts lands only while that attempt holds the parent's row, share-locked to
        -- commit after the parent's jobs row (docs/sql-internals.md, "Execution fence").
        IF p_parent_execution_number IS NOT NULL THEN
            PERFORM 1
            FROM {{schema}}.jobs_state pr
            WHERE
                pr.job_id = p_parent_job_id
                AND pr.job_status_code = 50 /* JobStatusCode.Executing */
                AND pr.execution_number = p_parent_execution_number
                AND pr.leased_by_worker_id IS NOT DISTINCT FROM p_parent_worker_id
            FOR SHARE;

            IF NOT FOUND THEN
                RAISE EXCEPTION 'ACTA:EXECUTION_FENCED:enqueue_job: parent job % is no longer executing attempt %.',
                    p_parent_job_id, p_parent_execution_number
                    USING ERRCODE = 'P0001';
            END IF;
        END IF;

        IF v_lineage IS NULL THEN
            RAISE EXCEPTION 'Enqueue rejected: one or more child rows reference a missing or terminal parent job.'
                USING ERRCODE = 'P0001';
        END IF;

        IF v_def_tenant_req = 10 /* DefinitionTenantRequirementCode.Required */ AND v_tenant_id IS NULL AND v_parent_tenant IS NULL THEN
            RAISE EXCEPTION 'ACTA:ENQ_TENANT_REQUIRED:Enqueue rejected: the job definition requires a tenant and the row carries none.'
                USING ERRCODE = 'P0001';
        END IF;

        IF v_tenant_id IS NOT NULL AND v_parent_tenant IS NOT NULL AND v_tenant_id <> v_parent_tenant AND NOT p_tenant_override THEN
            RAISE EXCEPTION 'ACTA:ENQ_TENANT_MISMATCH:Enqueue rejected: a child TenantName differs from the parent tenant without an explicit override.'
                USING ERRCODE = 'P0001';
        END IF;

        IF v_lane_id IS NOT NULL AND EXISTS (
            WITH RECURSIVE ancestors AS (
                SELECT a.job_id, a.parent_job_id
                FROM {{schema}}.jobs a
                WHERE a.job_id = p_parent_job_id
                UNION ALL
                SELECT a.job_id, a.parent_job_id
                FROM {{schema}}.jobs a
                INNER JOIN ancestors c ON a.job_id = c.parent_job_id
            )
            SELECT 1
            FROM ancestors c
            INNER JOIN {{schema}}.jobs_state ar ON ar.job_id = c.job_id
            WHERE
                ar.lane_id = v_lane_id
                AND ar.job_status_code IN (
                    10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                    30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                )
        ) THEN
            RAISE EXCEPTION 'ACTA:ENQ_ANCESTOR_LANE:Enqueue rejected: a child names the lane of an unfinished ancestor, so it would wait behind the ancestor that waits for it.'
                USING ERRCODE = 'P0001';
        END IF;

        INSERT INTO {{schema}}.jobs (
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
        VALUES (
            p_job_ref,
            v_lineage,
            p_parent_job_id,
            p_deduplication_key,
            COALESCE(p_correlation_key, v_parent_corr),
            v_ns_id,
            v_def_id,
            CASE WHEN v_def_tenant_req = 20 /* DefinitionTenantRequirementCode.Forbidden */ THEN NULL
                ELSE COALESCE(v_tenant_id, v_parent_tenant) END,
            COALESCE(p_input_format_id, CASE WHEN p_input IS NULL THEN 0 /* JobPayloadFormat.None */ ELSE 1 /* JobPayloadFormat.Json */ END),
            p_input,
            p_concurrency_key,
            v_def_event_recording,
            now())
        ON CONFLICT (parent_job_id, deduplication_key)
            WHERE deduplication_key IS NOT NULL AND parent_job_id IS NOT NULL
            DO NOTHING
        RETURNING jobs.job_id INTO v_job_id;
    ELSE
        IF v_def_tenant_req = 10 /* DefinitionTenantRequirementCode.Required */ AND v_tenant_id IS NULL THEN
            RAISE EXCEPTION 'ACTA:ENQ_TENANT_REQUIRED:Enqueue rejected: the job definition requires a tenant and the row carries none.'
                USING ERRCODE = 'P0001';
        END IF;

        INSERT INTO {{schema}}.jobs (
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
        VALUES (
            p_job_ref,
            NULL,
            NULL,
            p_deduplication_key,
            p_correlation_key,
            v_ns_id,
            v_def_id,
            v_tenant_id,
            COALESCE(p_input_format_id, CASE WHEN p_input IS NULL THEN 0 /* JobPayloadFormat.None */ ELSE 1 /* JobPayloadFormat.Json */ END),
            p_input,
            p_concurrency_key,
            v_def_event_recording,
            now())
        ON CONFLICT (namespace_id, deduplication_key)
            WHERE deduplication_key IS NOT NULL AND parent_job_id IS NULL
            DO NOTHING
        RETURNING jobs.job_id INTO v_job_id;
    END IF;

    IF v_job_id IS NOT NULL THEN
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
            version)
        VALUES (
            v_job_id,
            v_ns_id,
            v_lane_id,
            CASE
                WHEN v_lane_id IS NOT NULL AND EXISTS (
                    SELECT 1
                    FROM {{schema}}.jobs_state m
                    WHERE
                        m.lane_id = v_lane_id
                        AND m.lane_id IS NOT NULL
                        AND m.job_status_code IN (
                            10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                            30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                        )
                )
                    THEN 15 /* JobStatusCode.Blocked */
                ELSE 10 /* JobStatusCode.Ready */
            END,
            COALESCE(p_job_priority_code_override, v_def_priority),
            COALESCE(p_next_run_at_utc, now() + make_interval(secs => COALESCE(p_delay_seconds, 0))),
            0,
            0,
            NULL,
            now(),
            0);

        INSERT INTO {{schema}}.tags (entity_code, entity_id, tag_name, tag_value, tag_value_search)
        SELECT 50 /* EntityCode.Job */, v_job_id, t.tag_name, t.tag_value, t.tag_value_search
        FROM unnest(p_tag_name, p_tag_value, p_tag_value_search) AS t(tag_name, tag_value, tag_value_search);

        RETURN QUERY SELECT 0, v_job_id, p_job_ref, 1 /* EnqueueJobAction.Inserted */;
    ELSE
        RETURN QUERY
        SELECT 0, j.job_id, j.job_ref, 2 /* EnqueueJobAction.Deduplicated */
        FROM {{schema}}.jobs j
        WHERE
            (p_parent_job_id IS NULL
                AND j.namespace_id = v_ns_id
                AND j.deduplication_key = p_deduplication_key
                AND j.parent_job_id IS NULL)
            OR (p_parent_job_id IS NOT NULL
                AND j.parent_job_id = p_parent_job_id
                AND j.deduplication_key = p_deduplication_key);
    END IF;
END;
$$;