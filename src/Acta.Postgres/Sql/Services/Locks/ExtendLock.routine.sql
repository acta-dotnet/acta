CREATE OR REPLACE FUNCTION {{schema}}.extend_lock(
    p_lock_key VARCHAR,
    p_hold_token UUID,
    p_lease_ttl_seconds INT
)
RETURNS TABLE (hold_token UUID)
LANGUAGE sql
AS $$
    UPDATE {{schema}}.locks
    SET expires_at_utc = now() + (p_lease_ttl_seconds * INTERVAL '1 second')
    WHERE lock_key = p_lock_key AND hold_token = p_hold_token
    RETURNING hold_token;
$$;
