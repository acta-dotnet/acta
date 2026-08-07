CREATE OR REPLACE FUNCTION {{schema}}.extend_worker_leases(
    p_leased_by_worker_id INT,
    p_lease_ttl_seconds INT,
    p_draining BOOLEAN
)
RETURNS TABLE (job_id BIGINT)
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
    RETURN QUERY
    UPDATE {{schema}}.runtimes r
    SET lease_expires_at_utc = v_new_expiry
    WHERE
        r.leased_by_worker_id = p_leased_by_worker_id
        AND r.status_code IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */)
    RETURNING r.job_id;
END;
$$;
