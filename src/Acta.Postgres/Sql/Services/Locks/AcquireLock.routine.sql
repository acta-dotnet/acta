CREATE OR REPLACE FUNCTION {{schema}}.acquire_lock(
    p_lock_key VARCHAR,
    p_job_id BIGINT,
    p_lease_ttl_seconds INT,
    p_hold_token UUID
)
RETURNS TABLE (hold_token UUID)
LANGUAGE sql
AS $$
    INSERT INTO {{schema}}.locks (lock_key, job_id, expires_at_utc, hold_token)
    VALUES (p_lock_key, p_job_id, now() + (p_lease_ttl_seconds * INTERVAL '1 second'), p_hold_token)
    ON CONFLICT (lock_key) DO UPDATE SET
        job_id = EXCLUDED.job_id,
        expires_at_utc = EXCLUDED.expires_at_utc,
        hold_token = EXCLUDED.hold_token
    WHERE locks.expires_at_utc <= now()
    RETURNING hold_token;
$$;
