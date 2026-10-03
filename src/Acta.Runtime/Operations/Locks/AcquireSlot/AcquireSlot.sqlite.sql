-- A slot is free when it has no row or its hold expired; the insert steals an expired row in place.
-- The primary key arbitrates two racers on one slot, and the loser's upsert guard fails to no row.
WITH RECURSIVE slots (i) AS (
    SELECT 0
    UNION ALL
    SELECT i + 1 FROM slots WHERE i + 1 < @p_slot_count
),
free AS (
    SELECT s.i
    FROM slots s
    WHERE NOT EXISTS (
        SELECT 1
        FROM {{schema}}.locks held
        WHERE
            held.lock_key = @p_lock_key_prefix || '.' || s.i
            AND held.expires_at_utc > {{now}}
    )
    ORDER BY s.i
    LIMIT 1
)
INSERT INTO {{schema}}.locks (lock_key, job_id, expires_at_utc, hold_token)
SELECT
    @p_lock_key_prefix || '.' || free.i,
    @p_job_id,
    {{now}} + (@p_lease_ttl_seconds) * 1000,
    @p_hold_token
FROM free
WHERE true
ON CONFLICT (lock_key) DO UPDATE
SET
    job_id = excluded.job_id,
    expires_at_utc = excluded.expires_at_utc,
    hold_token = excluded.hold_token
WHERE locks.expires_at_utc <= {{now}}
RETURNING lock_key;
