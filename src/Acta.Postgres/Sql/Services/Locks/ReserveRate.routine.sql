-- GCRA with a reservation; the full contract is on ILockStore.ReserveRateAsync. A turn is honoured
-- only while it is fresh, at most one interval past its instant; one that went stale while executors
-- were busy goes back through the meter, so a queue of overdue jobs cannot all start at once.

-- The bucket row is created-or-locked by one statement below, so the expiry sweep can never delete
-- it between a create and a lock: the charge this call books is always persisted before it admits.
CREATE OR REPLACE FUNCTION {{schema}}.reserve_rate(
    p_lock_key VARCHAR,
    p_job_id BIGINT,
    p_rate_interval_ms INT,
    p_rate_burst INT,
    p_grace_seconds INT,
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
    -- A reservation row expires a grace after its turn rather than on it, so the sweep never takes a
    -- turn a live job is still coming back for.
    v_grace INTERVAL := make_interval(secs => p_grace_seconds);
    v_reservation VARCHAR := p_lock_key || '.' || p_job_id;
    v_stored TIMESTAMPTZ;
    v_due TIMESTAMPTZ;
    v_turn TIMESTAMPTZ;
BEGIN
    -- The bucket stores the arrival time plus the lookback, so the row expires exactly when an idle
    -- meter stops saying anything a missing one would not. A missing meter starts at now.

    -- Create-or-lock in one statement: the DO UPDATE (a no-op assignment) takes the row lock, and
    -- RETURNING hands back whichever value is now locked in - freshly inserted, or already there.
    INSERT INTO {{schema}}.locks (lock_key, job_id, expires_at_utc, hold_token)
    VALUES (p_lock_key, p_job_id, v_now, p_hold_token)
    ON CONFLICT (lock_key) DO UPDATE SET lock_key = EXCLUDED.lock_key
    RETURNING expires_at_utc INTO v_stored;

    -- The row count decides consumption, never a later absence: the sweep cannot slip between a read
    -- and the delete and give away a free admission. A stale turn is spent here too, then re-metered.
    DELETE FROM {{schema}}.locks
    WHERE
        lock_key = v_reservation
        AND expires_at_utc <= v_now + v_grace
    RETURNING expires_at_utc - v_grace INTO v_due;

    IF v_due IS NOT NULL AND v_due >= v_now - v_interval THEN
        -- Back on time: the bucket counted this job when it allocated the turn, so charging it again
        -- would meter one job twice.
        RETURN QUERY SELECT v_due, TRUE;
        RETURN;
    END IF;

    IF v_due IS NULL THEN
        -- A turn still ahead of now, handed back unchanged: the job is early, not owed a new instant.
        SELECT r.expires_at_utc - v_grace INTO v_turn
        FROM {{schema}}.locks r
        WHERE r.lock_key = v_reservation;

        IF v_turn IS NOT NULL THEN
            RETURN QUERY SELECT v_turn, FALSE;
            RETURN;
        END IF;
    END IF;

    v_stored := GREATEST(v_stored, v_now);
    v_turn := v_stored - v_lookback;

    UPDATE {{schema}}.locks
    SET
        job_id = p_job_id,
        expires_at_utc = v_stored + v_interval,
        hold_token = p_hold_token
    WHERE lock_key = p_lock_key;

    -- The row lock has been held since the create-or-lock statement above, so this can never miss;
    -- if it ever did, admitting without a persisted charge would be worse than failing the call.
    IF NOT FOUND THEN
        RAISE EXCEPTION 'reserve_rate: bucket % vanished while its row lock was held', p_lock_key;
    END IF;

    IF v_turn <= v_now THEN
        RETURN QUERY SELECT v_turn, TRUE;
        RETURN;
    END IF;

    INSERT INTO {{schema}}.locks (lock_key, job_id, expires_at_utc, hold_token)
    VALUES (v_reservation, p_job_id, v_turn + v_grace, p_hold_token)
    ON CONFLICT (lock_key) DO UPDATE SET
        job_id = EXCLUDED.job_id,
        expires_at_utc = EXCLUDED.expires_at_utc,
        hold_token = EXCLUDED.hold_token;

    RETURN QUERY SELECT v_turn, FALSE;
END;
$$;

-- CREATE OR REPLACE across arities creates an overload instead of replacing; drop the retired
-- signature (without the grace) so pre-existing installs cannot resolve the stale form.
DROP FUNCTION IF EXISTS {{schema}}.reserve_rate(VARCHAR, BIGINT, INT, INT, UUID);
