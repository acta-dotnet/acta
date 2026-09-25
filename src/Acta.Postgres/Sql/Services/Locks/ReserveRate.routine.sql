-- GCRA with a reservation; the full contract is on ILockStore.ReserveRateAsync. A turn is honoured
-- while it is fresh, one second past its instant (one interval when that is longer); a stale one goes
-- back through the meter, so a queue of overdue jobs cannot all start at once after a long stall.

-- The bucket row is created-or-locked by one statement below, so the expiry sweep can never delete
-- it between a create and a lock: the charge this call books is always persisted before it admits.

-- That no-op DO UPDATE writes a new row version per request, so a hot meter leaves autovacuum one
-- dead tuple per admission; the price of the single-statement lock, not a thing to optimize away.

-- The result shape changed when wait_ms joined it, and CREATE OR REPLACE cannot change a return
-- type, so an install still carrying the previous shape drops it first; a current one is left to the
-- REPLACE below, which keeps whatever grants an operator put on the function.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM pg_proc p
        INNER JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE
            n.nspname = '{{schema}}'
            AND p.proname = 'reserve_rate'
            AND p.pronargs = 6
            AND NOT ('wait_ms' = ANY (p.proargnames))
    ) THEN
        DROP FUNCTION {{schema}}.reserve_rate(VARCHAR, BIGINT, INT, INT, INT, UUID);
    END IF;
END
$$;
CREATE OR REPLACE FUNCTION {{schema}}.reserve_rate(
    p_lock_key VARCHAR,
    p_job_id BIGINT,
    p_rate_interval_ms INT,
    p_rate_burst INT,
    p_grace_seconds INT,
    p_hold_token UUID
)
RETURNS TABLE (resume_at_utc TIMESTAMPTZ, admitted BOOLEAN, wait_ms BIGINT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_now TIMESTAMPTZ;
    v_interval INTERVAL := p_rate_interval_ms * INTERVAL '1 millisecond';
    -- How long a booked turn stays valid past its instant: a second covers the claim-path pickup at
    -- any rate, and a slower meter keeps its whole interval, so the window is the larger of the two.
    v_window INTERVAL := GREATEST(v_interval, INTERVAL '1 second');
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
    VALUES (p_lock_key, p_job_id, clock_timestamp(), p_hold_token)
    ON CONFLICT (lock_key) DO UPDATE SET lock_key = EXCLUDED.lock_key
    RETURNING expires_at_utc INTO v_stored;

    -- The wall clock, read once the row is locked, never the transaction's start: judged against a
    -- stale instant, every call a convoy on this meter delayed would be admitted together the moment
    -- the lock freed, several times the contract in one second under a flood of metered claims.
    v_now := clock_timestamp();

    -- The row count decides consumption, never a later absence: the sweep cannot slip between a read
    -- and the delete and give away a free admission. A stale turn is spent here too, then re-metered.
    DELETE FROM {{schema}}.locks
    WHERE
        lock_key = v_reservation
        AND expires_at_utc <= v_now + v_grace
    RETURNING expires_at_utc - v_grace INTO v_due;

    IF v_due IS NOT NULL AND v_due >= v_now - v_window THEN
        -- Back on time: the bucket counted this job when it allocated the turn, so charging it again
        -- would meter one job twice.
        RETURN QUERY SELECT v_due, TRUE, 0::BIGINT;
        RETURN;
    END IF;

    IF v_due IS NULL THEN
        -- A turn still ahead of now, handed back unchanged: the job is early, not owed a new instant.
        SELECT r.expires_at_utc - v_grace INTO v_turn
        FROM {{schema}}.locks r
        WHERE r.lock_key = v_reservation;

        IF v_turn IS NOT NULL THEN
            RETURN QUERY SELECT v_turn, FALSE, CEIL(EXTRACT(EPOCH FROM (v_turn - v_now)) * 1000)::BIGINT;
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
        RETURN QUERY SELECT v_turn, TRUE, 0::BIGINT;
        RETURN;
    END IF;

    INSERT INTO {{schema}}.locks (lock_key, job_id, expires_at_utc, hold_token)
    VALUES (v_reservation, p_job_id, v_turn + v_grace, p_hold_token)
    ON CONFLICT (lock_key) DO UPDATE SET
        job_id = EXCLUDED.job_id,
        expires_at_utc = EXCLUDED.expires_at_utc,
        hold_token = EXCLUDED.hold_token;

    RETURN QUERY SELECT v_turn, FALSE, CEIL(EXTRACT(EPOCH FROM (v_turn - v_now)) * 1000)::BIGINT;
END;
$$;

-- CREATE OR REPLACE across arities creates an overload instead of replacing; drop the retired
-- signature (without the grace) so pre-existing installs cannot resolve the stale form.
DROP FUNCTION IF EXISTS {{schema}}.reserve_rate(VARCHAR, BIGINT, INT, INT, UUID);
