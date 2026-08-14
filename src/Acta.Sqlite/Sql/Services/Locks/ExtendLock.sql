UPDATE {{schema}}.locks
SET expires_at_utc = {{now}} + (@p_lease_ttl_seconds) * 1000
WHERE lock_key = @p_lock_key AND hold_token = @p_hold_token
RETURNING hold_token;
