CREATE OR REPLACE FUNCTION {{schema}}.extend_worker_leases(
    p_leased_by_worker_id INT,
    p_lease_ttl_seconds INT,
    p_draining BOOLEAN
)
RETURNS TABLE (job_id BIGINT, renewed BOOLEAN)
LANGUAGE plpgsql
AS $$
DECLARE
    v_new_expiry TIMESTAMPTZ := now() + (p_lease_ttl_seconds * INTERVAL '1 second');
BEGIN
    UPDATE {{schema}}.workers
    SET
        last_seen_at_utc = now(),
        status_code = CASE WHEN p_draining AND status_code = 10 /* WorkerStatusCode.Active */
            THEN 80 /* WorkerStatusCode.Draining */ ELSE status_code END,
        modified_at_utc = now(),
        version = version + 1
    WHERE id = p_leased_by_worker_id;

    /* Push every in-flight execution lease forward. Deliberately no version bump: a lease refresh
       is not a claim-generation change, so a buffered claim still passes the start CAS. */
    -- A row another transaction holds is skipped and reported unrenewed, so the renewal never waits
    -- (docs/internals/sql-execution-policy.md, "Explicit exceptions and maintenance").
    RETURN QUERY
    WITH inflight AS (
        SELECT r0.job_id
        FROM {{schema}}.runtimes r0
        WHERE
            r0.leased_by_worker_id = p_leased_by_worker_id
            AND r0.status_code IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */)
    ),
    locked AS (
        SELECT r1.job_id
        FROM {{schema}}.runtimes r1
        WHERE
            r1.job_id IN (SELECT i.job_id FROM inflight i)
            AND r1.leased_by_worker_id = p_leased_by_worker_id
            AND r1.status_code IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */)
        ORDER BY r1.job_id
        FOR UPDATE SKIP LOCKED
    ),
    extended AS (
        UPDATE {{schema}}.runtimes r
        SET lease_expires_at_utc = v_new_expiry
        WHERE r.job_id IN (SELECT l.job_id FROM locked l)
        RETURNING r.job_id
    )
    SELECT i.job_id, e.job_id IS NOT NULL
    FROM inflight i
    LEFT JOIN extended e ON e.job_id = i.job_id;
END;
$$;
