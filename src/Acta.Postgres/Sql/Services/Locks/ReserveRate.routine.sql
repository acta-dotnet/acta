-- GCRA with a reservation; the contract is on ILockStore.ReserveRateAsync. Locking: the bucket
-- upsert's own row lock serializes two requesters and is held to commit, so neither reads the
-- other's arrival time, and ON CONFLICT DO UPDATE re-reads the winner's committed row.
CREATE OR REPLACE FUNCTION {{schema}}.reserve_rate(
    p_lock_key VARCHAR,
    p_job_id BIGINT,
    p_rate_interval_ms INT,
    p_rate_burst INT,
    p_hold_token UUID
)
RETURNS TABLE (resume_at_utc TIMESTAMPTZ, admitted BOOLEAN)
LANGUAGE plpgsql
AS $$
DECLARE
    v_now TIMESTAMPTZ := now();
    v_interval INTERVAL := p_rate_interval_ms * INTERVAL '1 millisecond';
    -- How far behind now an idle meter is allowed to be, which is what hands out the burst: one
    -- interval short of a whole period, so the burst-th request lands on now and the next one waits.
    v_lookback INTERVAL := (p_rate_burst - 1) * (p_rate_interval_ms * INTERVAL '1 millisecond');
    v_reservation VARCHAR := p_lock_key || '.' || p_job_id;
BEGIN
    -- The bucket stores the arrival time plus the lookback, so the row expires exactly when it stops
    -- saying anything a missing row would not. Skipped for a job that already holds a reservation:
    -- that request moved the bucket when it booked, so its turn is paid for.
    INSERT INTO {{schema}}.locks (lock_key, job_id, expires_at_utc, hold_token)
    SELECT p_lock_key, p_job_id, v_now + v_interval, p_hold_token
    WHERE NOT EXISTS (
        SELECT 1
        FROM {{schema}}.locks r
        WHERE r.lock_key = v_reservation)
    ON CONFLICT (lock_key) DO UPDATE SET
        job_id = EXCLUDED.job_id,
        expires_at_utc = GREATEST(locks.expires_at_utc, v_now) + v_interval,
        hold_token = EXCLUDED.hold_token;

    -- The arrival time this request took is the stored value minus the interval it just added and the
    -- lookback. Book it when it is still ahead of now, so the caller re-arms once instead of racing.
    INSERT INTO {{schema}}.locks (lock_key, job_id, expires_at_utc, hold_token)
    SELECT v_reservation, p_job_id, b.expires_at_utc - v_interval - v_lookback, p_hold_token
    FROM {{schema}}.locks b
    WHERE
        b.lock_key = p_lock_key
        AND b.expires_at_utc - v_interval - v_lookback > v_now
        AND NOT EXISTS (
            SELECT 1
            FROM {{schema}}.locks r
            WHERE r.lock_key = v_reservation)
    ON CONFLICT (lock_key) DO NOTHING;

    -- A reservation whose instant has arrived is consumed here, which is what makes admission
    -- once-only: a second attempt of the same job books a fresh turn instead of reusing this one.
    DELETE FROM {{schema}}.locks
    WHERE
        lock_key = v_reservation
        AND expires_at_utc <= v_now;

    -- Whether a reservation survived the three statements above is the whole answer: none left means
    -- the turn is now.
    RETURN QUERY
    SELECT
        COALESCE((SELECT r.expires_at_utc FROM {{schema}}.locks r WHERE r.lock_key = v_reservation), v_now),
        NOT EXISTS (SELECT 1 FROM {{schema}}.locks r WHERE r.lock_key = v_reservation);
END;
$$;
