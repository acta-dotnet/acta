-- Only p_namespace_name and p_job_name are required; other params default (p_job_ref is server-generated
-- when omitted; p_input_format_id defaults json/none by input presence). Parameter ORDER is fixed:
-- the provider store invokes this function positionally.
CREATE OR REPLACE FUNCTION {{schema}}.enqueue_one(
    p_job_ref UUID DEFAULT GEN_RANDOM_UUID(),
    p_namespace_name VARCHAR DEFAULT NULL,
    p_job_name VARCHAR DEFAULT NULL,
    p_deduplication_key VARCHAR DEFAULT NULL,
    p_correlation_key VARCHAR DEFAULT NULL,
    p_priority_override SMALLINT DEFAULT NULL,
    p_input_format_id SMALLINT DEFAULT NULL,
    p_input BYTEA DEFAULT NULL,
    p_exclusive_key VARCHAR DEFAULT NULL,
    p_next_run_at_utc TIMESTAMPTZ DEFAULT NULL,
    p_delay_seconds INT DEFAULT NULL,
    p_parent_id BIGINT DEFAULT NULL,
    p_tenant_key VARCHAR DEFAULT NULL,
    p_tenant_override BOOLEAN DEFAULT FALSE,
    p_t_name VARCHAR [] DEFAULT NULL,
    p_t_value VARCHAR [] DEFAULT NULL,
    p_t_value_search VARCHAR [] DEFAULT NULL
)
RETURNS TABLE (ordinal INT, job_id BIGINT, job_ref UUID, action INT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_ns_id INT;
    v_ns_status SMALLINT;
    v_def_id INT;
    v_def_priority SMALLINT;
    v_def_audit SMALLINT;
    v_def_status SMALLINT;
    v_def_tenant_req SMALLINT;
    v_tenant_id INT;
    v_tenant_status SMALLINT;
    v_lineage BIGINT;
    v_parent_corr VARCHAR;
    v_parent_tenant INT;
    v_job_id BIGINT;
BEGIN
    SELECT ns.id, ns.status_code, jd.id, jd.priority_code_effective, jd.audit_level_code_effective, jd.status_code, jd.tenant_requirement_code
    INTO v_ns_id, v_ns_status, v_def_id, v_def_priority, v_def_audit, v_def_status, v_def_tenant_req
    FROM {{schema}}.namespaces ns
    INNER JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.id AND jd.name = p_job_name
    WHERE ns.name = p_namespace_name;

    IF v_def_id IS NULL THEN
        RAISE EXCEPTION 'ACTA:ENQ_ROUTE_UNKNOWN:Enqueue rejected: one or more rows reference an unknown namespace or job. Has the owning worker run InitializeAsync yet?'
            USING ERRCODE = 'P0001';
    END IF;

    IF v_ns_status <> 10 /* NamespaceStatusCode.Active */ THEN
        RAISE EXCEPTION 'ACTA:ENQ_NS_SUSPENDED:Enqueue rejected: one or more rows reference a suspended namespace.'
            USING ERRCODE = 'P0001';
    END IF;

    IF v_def_status <> 10 /* JobDefinitionStatusCode.Active */ THEN
        RAISE EXCEPTION 'ACTA:ENQ_DEF_RETIRED:Enqueue rejected: the job definition is retired.'
            USING ERRCODE = 'P0001';
    END IF;

    IF p_tenant_key IS NOT NULL THEN
        SELECT t.id, t.status_code INTO v_tenant_id, v_tenant_status
        FROM {{schema}}.tenants t
        WHERE t.tenant_key = p_tenant_key;

        IF v_tenant_id IS NULL THEN
            RAISE EXCEPTION 'ACTA:ENQ_TENANT_UNKNOWN:Enqueue rejected: one or more rows reference an unknown tenant.'
                USING ERRCODE = 'P0001';
        END IF;

        IF v_tenant_status <> 10 /* TenantStatusCode.Active */ THEN
            RAISE EXCEPTION 'ACTA:ENQ_TENANT_SUSPENDED:Enqueue rejected: one or more rows reference a suspended tenant.'
                USING ERRCODE = 'P0001';
        END IF;
    END IF;

    IF v_def_tenant_req = 20 /* JobTenantRequirementCode.Forbidden */ AND p_tenant_key IS NOT NULL THEN
        RAISE EXCEPTION 'ACTA:ENQ_TENANT_FORBIDDEN:Enqueue rejected: the job definition forbids a tenant and the row names one.'
            USING ERRCODE = 'P0001';
    END IF;

    IF p_parent_id IS NOT NULL THEN
        SELECT COALESCE(pj.lineage_root_id, pj.id), pj.correlation_key, pj.tenant_id
        INTO v_lineage, v_parent_corr, v_parent_tenant
        FROM {{schema}}.jobs pj
        INNER JOIN {{schema}}.runtimes pr ON pr.job_id = pj.id
        WHERE
            pj.id = p_parent_id
            AND pr.status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
        FOR UPDATE OF pj;

        IF v_lineage IS NULL THEN
            RAISE EXCEPTION 'Enqueue rejected: one or more child rows reference a missing or terminal parent job.'
                USING ERRCODE = 'P0001';
        END IF;

        IF v_def_tenant_req = 10 /* JobTenantRequirementCode.Required */ AND v_tenant_id IS NULL AND v_parent_tenant IS NULL THEN
            RAISE EXCEPTION 'ACTA:ENQ_TENANT_REQUIRED:Enqueue rejected: the job definition requires a tenant and the row carries none.'
                USING ERRCODE = 'P0001';
        END IF;

        IF v_tenant_id IS NOT NULL AND v_parent_tenant IS NOT NULL AND v_tenant_id <> v_parent_tenant AND NOT p_tenant_override THEN
            RAISE EXCEPTION 'ACTA:ENQ_TENANT_MISMATCH:Enqueue rejected: a child TenantKey differs from the parent tenant without an explicit override.'
                USING ERRCODE = 'P0001';
        END IF;

        INSERT INTO {{schema}}.jobs (
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
        VALUES (
            p_job_ref,
            v_lineage,
            p_parent_id,
            p_deduplication_key,
            COALESCE(p_correlation_key, v_parent_corr),
            v_ns_id,
            v_def_id,
            CASE WHEN v_def_tenant_req = 20 /* JobTenantRequirementCode.Forbidden */ THEN NULL
                ELSE COALESCE(v_tenant_id, v_parent_tenant) END,
            COALESCE(p_input_format_id, CASE WHEN p_input IS NULL THEN 0 /* JobPayloadFormat.None */ ELSE 1 /* JobPayloadFormat.Json */ END),
            p_input,
            p_exclusive_key,
            v_def_audit,
            now())
        ON CONFLICT (parent_id, deduplication_key)
            WHERE deduplication_key IS NOT NULL AND parent_id IS NOT NULL
            DO NOTHING
        RETURNING id INTO v_job_id;
    ELSE
        IF v_def_tenant_req = 10 /* JobTenantRequirementCode.Required */ AND v_tenant_id IS NULL THEN
            RAISE EXCEPTION 'ACTA:ENQ_TENANT_REQUIRED:Enqueue rejected: the job definition requires a tenant and the row carries none.'
                USING ERRCODE = 'P0001';
        END IF;

        INSERT INTO {{schema}}.jobs (
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
            p_exclusive_key,
            v_def_audit,
            now())
        ON CONFLICT (namespace_id, deduplication_key)
            WHERE deduplication_key IS NOT NULL AND parent_id IS NULL
            DO NOTHING
        RETURNING id INTO v_job_id;
    END IF;

    IF v_job_id IS NOT NULL THEN
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
        VALUES (
            v_job_id,
            v_ns_id,
            10 /* JobStatusCode.Ready */,
            COALESCE(p_priority_override, v_def_priority),
            COALESCE(p_next_run_at_utc, now() + make_interval(secs => COALESCE(p_delay_seconds, 0))),
            0,
            0,
            NULL,
            now(),
            0);

        INSERT INTO {{schema}}.tags (scope_code, scope_id, namespace_id, name, value, value_search)
        SELECT 50 /* TagScopeCode.Job */, v_job_id, v_ns_id, t.name, t.value, t.value_search
        FROM unnest(p_t_name, p_t_value, p_t_value_search) AS t(name, value, value_search);

        RETURN QUERY SELECT 0, v_job_id, p_job_ref, 1 /* JobEnqueueAction.Inserted */;
    ELSE
        RETURN QUERY
        SELECT 0, j.id, j.job_ref, 2 /* JobEnqueueAction.Deduplicated */
        FROM {{schema}}.jobs j
        WHERE
            (p_parent_id IS NULL
                AND j.namespace_id = v_ns_id
                AND j.deduplication_key = p_deduplication_key
                AND j.parent_id IS NULL)
            OR (p_parent_id IS NOT NULL
                AND j.parent_id = p_parent_id
                AND j.deduplication_key = p_deduplication_key);
    END IF;
END;
$$;

-- CREATE OR REPLACE across arities creates an overload instead of replacing; drop the retired
-- signature (without p_tenant_override) so pre-existing installs cannot resolve the stale form.
DROP FUNCTION IF EXISTS {{schema}}.enqueue_one(
    UUID, VARCHAR, VARCHAR, VARCHAR, VARCHAR, SMALLINT, SMALLINT, BYTEA, VARCHAR, TIMESTAMPTZ, INT,
    BIGINT, VARCHAR, VARCHAR [], VARCHAR [], VARCHAR []
);
