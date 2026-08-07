UPDATE {{schema}}.leases
SET expires_at_utc = {{now}} + (@p_lease_ttl_seconds) * 1000
WHERE lease_key = @p_lease_key AND version = @p_version
RETURNING version;
