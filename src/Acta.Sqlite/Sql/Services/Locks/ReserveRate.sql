-- GCRA with a reservation; the full contract is on ILockStore.ReserveRateAsync. A turn is honoured
-- only while it is fresh, at most one interval past its instant; one that went stale while executors
-- were busy goes back through the meter, so a queue of overdue jobs cannot all start at once.

-- The batch runs inside the session's immediate write transaction, SQLite's single writer, so every
-- statement here sees one serialized meter without a row hint and the sweep cannot race the
-- read-then-delete of a turn below.
DROP TABLE IF EXISTS temp._rate_step;

-- The bucket stores the arrival time plus the lookback, so the row expires exactly when an idle meter
-- stops saying anything a missing one would not. A missing meter starts at now.
INSERT INTO {{schema}}.locks (lock_key, job_id, expires_at_utc, hold_token)
SELECT @p_lock_key, @p_job_id, {{now}}, @p_hold_token
WHERE NOT EXISTS (
    SELECT 1
    FROM {{schema}}.locks b
    WHERE b.lock_key = @p_lock_key);

-- One clock reading and one decision for the whole batch: whether this job holds a turn that has come
-- (and how stale it is), a turn still ahead of now, or nothing at all.
CREATE TEMP TABLE _rate_step AS
SELECT
    now_ms,
    consumed_due,
    CASE
        WHEN consumed_due IS NOT NULL AND consumed_due >= now_ms - @p_rate_interval_ms THEN 0
        WHEN consumed_due IS NULL AND future_due IS NOT NULL THEN 0
        ELSE 1
    END AS meter
FROM (
    SELECT
        {{now}} AS now_ms,
        (SELECT r.expires_at_utc - @p_grace_seconds * 1000
            FROM {{schema}}.locks r
            WHERE
                r.lock_key = @p_lock_key || '.' || @p_job_id
                AND r.expires_at_utc <= {{now}} + @p_grace_seconds * 1000) AS consumed_due,
        (SELECT r.expires_at_utc - @p_grace_seconds * 1000
            FROM {{schema}}.locks r
            WHERE
                r.lock_key = @p_lock_key || '.' || @p_job_id
                AND r.expires_at_utc > {{now}} + @p_grace_seconds * 1000) AS future_due);

-- A turn that has come is spent here whether or not it is still fresh, so a stale one cannot be
-- reused: the job either runs now or takes a new instant below.
DELETE FROM {{schema}}.locks
WHERE
    lock_key = @p_lock_key || '.' || @p_job_id
    AND expires_at_utc <= (SELECT s.now_ms FROM _rate_step s) + @p_grace_seconds * 1000;

-- Back on time, or early with a turn still booked, leaves the meter alone: the bucket counted this
-- job when it allocated the turn, so charging it again would meter one job twice.
UPDATE {{schema}}.locks
SET
    job_id = @p_job_id,
    expires_at_utc = MAX(expires_at_utc, (SELECT s.now_ms FROM _rate_step s)) + @p_rate_interval_ms,
    hold_token = @p_hold_token
WHERE
    lock_key = @p_lock_key
    AND (SELECT s.meter FROM _rate_step s) = 1;

-- The instant this request took is the stored value minus the interval it just added and the
-- lookback. Book it when it is still ahead of now, a grace past the turn so the sweep leaves it be.
INSERT INTO {{schema}}.locks (lock_key, job_id, expires_at_utc, hold_token)
SELECT
    @p_lock_key || '.' || @p_job_id,
    @p_job_id,
    b.expires_at_utc - @p_rate_interval_ms - (@p_rate_burst - 1) * @p_rate_interval_ms + @p_grace_seconds * 1000,
    @p_hold_token
FROM {{schema}}.locks b, _rate_step s
WHERE
    b.lock_key = @p_lock_key
    AND s.meter = 1
    AND b.expires_at_utc - @p_rate_interval_ms - (@p_rate_burst - 1) * @p_rate_interval_ms > s.now_ms
ON CONFLICT (lock_key) DO UPDATE
SET
    job_id = excluded.job_id,
    expires_at_utc = excluded.expires_at_utc,
    hold_token = excluded.hold_token;

-- Whether a reservation survived the statements above is the whole answer: none left means the turn
-- is now.
SELECT
    COALESCE(
        (SELECT r.expires_at_utc - @p_grace_seconds * 1000
            FROM {{schema}}.locks r
            WHERE r.lock_key = @p_lock_key || '.' || @p_job_id),
        (SELECT s.consumed_due FROM _rate_step s),
        (SELECT s.now_ms FROM _rate_step s)
    ) AS resume_at_utc,
    CASE
        WHEN EXISTS (SELECT 1 FROM {{schema}}.locks r WHERE r.lock_key = @p_lock_key || '.' || @p_job_id) THEN 0
        ELSE 1
    END AS admitted;
