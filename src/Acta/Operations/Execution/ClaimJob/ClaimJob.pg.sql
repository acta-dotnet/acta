WITH candidates AS (
    /* claim_jobs' due test: a Ready row always carries its due instant (ck_jobs_state_ready_due), and a
       Suspended row with a NULL instant is an unbounded wait that never comes due. */
    SELECT r.job_id
    FROM {{schema}}.jobs_state r
    WHERE
        r.job_id = @p_job_id
        AND r.namespace_id = @p_namespace_id
        AND r.job_status_code IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */)
        AND r.next_run_at_utc IS NOT NULL
        AND r.next_run_at_utc <= now()
    FOR UPDATE OF r SKIP LOCKED
),
updated AS (
    UPDATE {{schema}}.jobs_state r
    SET
        job_status_code = 50 /* JobStatusCode.Executing */,
        execution_number = r.execution_number + 1,
        leased_by_worker_id = @p_leased_by_worker_id,
        lease_expires_at_utc = now() + (@p_lease_ttl_seconds * INTERVAL '1 second'),
        result_format_id = 0 /* JobPayloadFormat.None */,
        result = NULL,
        modified_at_utc = now(),
        version = r.version + 1
    FROM candidates c
    JOIN {{schema}}.jobs j ON j.job_id = c.job_id
    WHERE r.job_id = c.job_id
    RETURNING
        r.job_id, j.definition_id, j.tenant_id,
        r.execution_number, j.deduplication_key, j.job_kind_code, j.correlation_key, j.concurrency_key,
        j.input_format_id, j.input, r.next_run_at_utc, j.created_at_utc,
        r.failure_count, j.job_ref, r.lane_id, j.parent_job_id
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
    u.lane_id,
    u.parent_job_id
FROM updated u;
