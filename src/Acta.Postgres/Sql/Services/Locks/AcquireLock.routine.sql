CREATE OR REPLACE FUNCTION {{schema}}.acquire_lock(
    p_lease_key VARCHAR,
    p_job_id BIGINT,
    p_lease_ttl_seconds INT
)
RETURNS TABLE (version INT)
LANGUAGE sql
AS $$
    INSERT INTO {{schema}}.leases (lease_key, kind_code, job_id, expires_at_utc, version)
    VALUES (p_lease_key, 10 /* LeaseKindCode.Lock */, p_job_id, now() + (p_lease_ttl_seconds * INTERVAL '1 second'), 1)
    ON CONFLICT (lease_key) DO UPDATE SET
        job_id = EXCLUDED.job_id,
        expires_at_utc = EXCLUDED.expires_at_utc,
        version = leases.version + 1
    WHERE leases.expires_at_utc <= now()
    RETURNING version;
$$;
