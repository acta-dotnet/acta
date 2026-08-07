CREATE OR REPLACE FUNCTION {{schema}}.extend_lock(
    p_lease_key VARCHAR,
    p_version INT,
    p_lease_ttl_seconds INT
)
RETURNS TABLE (version INT)
LANGUAGE sql
AS $$
    UPDATE {{schema}}.leases
    SET expires_at_utc = now() + (p_lease_ttl_seconds * INTERVAL '1 second')
    WHERE lease_key = p_lease_key AND version = p_version
    RETURNING version;
$$;
