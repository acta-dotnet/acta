CREATE OR REPLACE FUNCTION {{schema}}.claim_jobs(
    p_namespace_id INT,
    p_leased_by_worker_id INT,
    p_claim_limit INT,
    p_lease_ttl_seconds INT,
    p_excluded_definition_ids INT[] DEFAULT '{}',
    p_claim_from_utc TIMESTAMPTZ DEFAULT NULL
)
RETURNS TABLE (
    job_id BIGINT,
    definition_id INT,
    execution_number INT,
    deduplication_key VARCHAR,
    job_kind_code SMALLINT,
    correlation_key VARCHAR,
    concurrency_key VARCHAR,
    input_format_id SMALLINT,
    input BYTEA,
    next_run_at_utc TIMESTAMPTZ,
    created_at_utc TIMESTAMPTZ,
    failure_count INT,
    job_ref UUID,
    tenant_id INT,
    db_now TIMESTAMPTZ,
    next_ready_at_utc TIMESTAMPTZ,
    lane_id BIGINT,
    parent_job_id BIGINT,
    schedule_id INT
)
LANGUAGE sql
AS $$
    WITH bands AS (
        /* Every JobPriorityCode, listed once: the claim seeks each band and the horizon reads each band's head. */
        SELECT
            ARRAY[
                100 /* JobPriorityCode.Realtime */,
                85 /* JobPriorityCode.Critical */,
                70 /* JobPriorityCode.High */,
                50 /* JobPriorityCode.Normal */,
                0 /* JobPriorityCode.Bulk */
            ]::smallint[] AS job_priority_codes
    ),
    candidates AS (
        /* Pure claim-index scan on ix_jobs_state_claim_ready via the denormalized namespace; concurrency-key admission is executor-owned
           (lock store) after the start CAS, and the one jobs lookup is the exclusion below. */
        SELECT r.job_id
        FROM {{schema}}.jobs_state r
        WHERE
            r.namespace_id = p_namespace_id
            /* One array key per band: the scan seeks each band, leaves it at its first row not yet due, and still
               returns index order, so the ORDER BY needs no sort. */
            AND r.job_priority_code = ANY ((SELECT b.job_priority_codes FROM bands b)::smallint[])
            /* ix_jobs_state_claim_ready's filter restated whole: a partial index is matched only against top-level
               AND-terms. Ready always carries its due instant (ck_jobs_state_ready_due); a Suspended NULL never comes due. */
            AND r.job_status_code IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */)
            AND r.next_run_at_utc IS NOT NULL
            AND r.next_run_at_utc <= now()
            /* The worker's watermark starts each band's scan past the dead entries earlier claims left at its head
               (docs/sql-internals.md, "Claim watermark"); NULL scans from the head. */
            AND r.next_run_at_utc >= COALESCE(p_claim_from_utc, '-infinity'::timestamptz)
            /* Rolling-deploy exclusion: definitions this worker already bounced for want of a handler.
               Empty on a healthy fleet, and the cardinality test is evaluated first, so a healthy claim
               never reaches the jobs lookup. */
            AND (
                cardinality(p_excluded_definition_ids) = 0
                OR NOT EXISTS (
                    SELECT 1
                    FROM {{schema}}.jobs j
                    WHERE j.job_id = r.job_id AND j.definition_id = ANY (p_excluded_definition_ids)
                )
            )
        ORDER BY
            r.job_priority_code DESC,
            r.next_run_at_utc ASC,
            r.job_id ASC
        LIMIT p_claim_limit
        FOR UPDATE OF r SKIP LOCKED
    ),
    updated AS (
        UPDATE {{schema}}.jobs_state r
        SET
            job_status_code = 50 /* JobStatusCode.Executing */,
            execution_number = r.execution_number + 1,
            leased_by_worker_id = p_leased_by_worker_id,
            lease_expires_at_utc = now() + (p_lease_ttl_seconds * INTERVAL '1 second'),
            result_format_id = 0 /* JobPayloadFormat.None */,
            result = NULL,
            modified_at_utc = now(),
            version = r.version + 1
        FROM candidates c
        JOIN {{schema}}.jobs j ON j.job_id = c.job_id
        WHERE r.job_id = c.job_id
        RETURNING
            r.job_id,
            j.definition_id,
            j.tenant_id,
            r.execution_number,
            j.deduplication_key,
            j.job_kind_code,
            j.correlation_key,
            j.concurrency_key,
            j.input_format_id,
            j.input,
            r.next_run_at_utc,
            j.created_at_utc,
            r.failure_count,
            j.job_ref,
            r.lane_id,
            j.parent_job_id,
            j.schedule_id
    ),
    clock AS (
        SELECT now() AS db_now
    )
    SELECT
        u.job_id,
        u.definition_id,
        u.execution_number,
        u.deduplication_key,
        u.job_kind_code,
        u.correlation_key,
        u.concurrency_key,
        u.input_format_id,
        u.input,
        u.next_run_at_utc,
        u.created_at_utc,
        u.failure_count,
        u.job_ref,
        u.tenant_id,
        NULL::timestamptz AS db_now,
        NULL::timestamptz AS next_ready_at_utc,
        u.lane_id,
        u.parent_job_id,
        u.schedule_id
    FROM updated u
    UNION ALL
    SELECT
        NULL::bigint,
        NULL::int,
        NULL::int,
        NULL::varchar,
        NULL::smallint,
        NULL::varchar,
        NULL::varchar,
        NULL::smallint,
        NULL::bytea,
        NULL::timestamptz,
        NULL::timestamptz,
        NULL::smallint,
        NULL::uuid,
        NULL::int,
        c.db_now,
        /* The earliest head across the bands: each band's first index row is its earliest instant. */
        (SELECT MIN(head.next_run_at_utc)
            FROM unnest((SELECT b.job_priority_codes FROM bands b)) AS band (job_priority_code)
            CROSS JOIN LATERAL (
                SELECT r.next_run_at_utc
                FROM {{schema}}.jobs_state r
                WHERE
                    r.namespace_id = p_namespace_id
                    AND r.job_priority_code = band.job_priority_code
                    AND r.job_status_code IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */)
                    AND r.next_run_at_utc IS NOT NULL
                    /* Excluded rows are invisible to this worker's horizon too: a horizon at or before now
                       is what tells the caller to retry at the anti-spin floor, so counting rows this
                       worker will never claim would spin it. */
                    AND (
                        cardinality(p_excluded_definition_ids) = 0
                        OR NOT EXISTS (
                            SELECT 1
                            FROM {{schema}}.jobs j
                            WHERE j.job_id = r.job_id AND j.definition_id = ANY (p_excluded_definition_ids)
                        )
                    )
                ORDER BY r.next_run_at_utc
                LIMIT 1
            ) head),
        NULL::bigint,
        NULL::bigint,
        NULL::int
    FROM clock c
    WHERE NOT EXISTS (SELECT 1 FROM updated)
    ORDER BY job_id NULLS LAST;
$$;