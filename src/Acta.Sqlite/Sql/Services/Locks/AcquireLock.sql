INSERT INTO {{schema}}.locks (lock_key, job_id, expires_at_utc, hold_token)
VALUES (
    @p_lock_key,
    @p_job_id,
    {{now}} + (@p_lease_ttl_seconds) * 1000,
    @p_hold_token
)
ON CONFLICT (lock_key) DO UPDATE
SET
    job_id = excluded.job_id,
    expires_at_utc = excluded.expires_at_utc,
    hold_token = excluded.hold_token
WHERE locks.expires_at_utc <= {{now}}
RETURNING hold_token;
