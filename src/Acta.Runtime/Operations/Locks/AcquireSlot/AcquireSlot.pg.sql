-- A slot is free when it has no row or its hold expired; the insert steals an expired row in place.
-- The primary key arbitrates two racers on one slot, and the loser's ON CONFLICT guard fails to no row.
CREATE OR REPLACE FUNCTION {{schema}}.acquire_slot(
    p_lock_key_prefix VARCHAR,
    p_slot_count INT,
    p_job_id BIGINT,
    p_lease_ttl_seconds INT,
    p_hold_token UUID
)
RETURNS TABLE (slot_key VARCHAR)
LANGUAGE sql
AS $$
    WITH free AS (
        SELECT s.i
        FROM generate_series(0, p_slot_count - 1) AS s(i)
        WHERE NOT EXISTS (
            SELECT 1
            FROM {{schema}}.locks held
            WHERE
                held.lock_key = p_lock_key_prefix || '.' || s.i
                AND held.expires_at_utc > now()
        )
        ORDER BY s.i
        LIMIT 1
    )
    INSERT INTO {{schema}}.locks (lock_key, job_id, expires_at_utc, hold_token)
    SELECT
        p_lock_key_prefix || '.' || free.i,
        p_job_id,
        now() + (p_lease_ttl_seconds * INTERVAL '1 second'),
        p_hold_token
    FROM free
    ON CONFLICT (lock_key) DO UPDATE SET
        job_id = EXCLUDED.job_id,
        expires_at_utc = EXCLUDED.expires_at_utc,
        hold_token = EXCLUDED.hold_token
    WHERE locks.expires_at_utc <= now()
    RETURNING locks.lock_key;
$$;
