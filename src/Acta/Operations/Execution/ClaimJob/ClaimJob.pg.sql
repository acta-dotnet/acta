WITH candidates AS (
    /* claim_jobs' due test: a Ready row always carries its due instant (ck_runtimes_ready_due), and a
       Suspended row with a NULL instant is an unbounded wait that never comes due. */
    SELECT r.job_id AS id
    FROM {{schema}}.runtimes r
    WHERE
        r.job_id = @p_id
        AND r.namespace_id = @p_namespace_id
        AND r.status_code IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */)
        AND r.next_run_at_utc IS NOT NULL
        AND r.next_run_at_utc <= now()
    FOR UPDATE OF r SKIP LOCKED
),
updated AS (
    UPDATE {{schema}}.runtimes r
    SET
        status_code = 50 /* JobStatusCode.Executing */,
        execution_number = r.execution_number + 1,
        leased_by_worker_id = @p_leased_by_worker_id,
        lease_expires_at_utc = now() + (@p_lease_ttl_seconds * INTERVAL '1 second'),
        modified_at_utc = now(),
        version = r.version + 1
    FROM candidates c
    JOIN {{schema}}.jobs j ON j.id = c.id
    WHERE r.job_id = c.id
    RETURNING
        r.job_id AS id, j.namespace_id, j.definition_id, j.tenant_id,
        r.execution_number, j.deduplication_key, j.correlation_key, j.concurrency_key,
        j.input_format_id, j.input, r.next_run_at_utc, j.created_at_utc,
        r.failure_count, j.job_ref, r.lane_id, j.parent_id
)
SELECT
    u.id,
    u.namespace_id,
    u.definition_id,
    u.execution_number,
    u.deduplication_key,
    u.correlation_key,
    u.concurrency_key,
    u.input_format_id,
    u.input,
    u.next_run_at_utc,
    now() + (@p_lease_ttl_seconds * INTERVAL '1 second') AS lease_expires_at_utc,
    u.created_at_utc,
    u.failure_count,
    u.job_ref,
    u.tenant_id,
    NULL::timestamptz AS db_now,
    NULL::timestamptz AS next_ready_at_utc,
    u.lane_id,
    u.parent_id
FROM updated u;
