-- GCRA with a reservation; the contract is on ILockStore.ReserveRateAsync. Locking: the batch runs
-- inside the session's immediate write transaction, SQLite's single writer, so two requesters never
-- interleave here and the bucket needs no hint of its own.

-- The bucket stores the arrival time plus the lookback, so the row expires exactly when it stops
-- saying anything a missing row would not. Skipped for a job that already holds a reservation: that
-- request moved the bucket when it booked, so its turn is paid for.
INSERT INTO {{schema}}.locks (lock_key, job_id, expires_at_utc, hold_token)
SELECT
    @p_lock_key,
    @p_job_id,
    MAX(COALESCE((SELECT b.expires_at_utc FROM {{schema}}.locks b WHERE b.lock_key = @p_lock_key), {{now}}), {{now}})
        + @p_rate_interval_ms,
    @p_hold_token
WHERE NOT EXISTS (
    SELECT 1
    FROM {{schema}}.locks r
    WHERE r.lock_key = @p_lock_key || '.' || @p_job_id)
ON CONFLICT (lock_key) DO UPDATE
SET
    job_id = excluded.job_id,
    expires_at_utc = excluded.expires_at_utc,
    hold_token = excluded.hold_token;

-- The arrival time this request took is the stored value minus the interval it just added and the
-- lookback. Book it when it is still ahead of now, so the caller re-arms once instead of racing.
INSERT INTO {{schema}}.locks (lock_key, job_id, expires_at_utc, hold_token)
SELECT
    @p_lock_key || '.' || @p_job_id,
    @p_job_id,
    b.expires_at_utc - @p_rate_interval_ms - (@p_rate_burst - 1) * @p_rate_interval_ms,
    @p_hold_token
FROM {{schema}}.locks b
WHERE
    b.lock_key = @p_lock_key
    AND b.expires_at_utc - @p_rate_interval_ms - (@p_rate_burst - 1) * @p_rate_interval_ms > {{now}}
    AND NOT EXISTS (
        SELECT 1
        FROM {{schema}}.locks r
        WHERE r.lock_key = @p_lock_key || '.' || @p_job_id)
ON CONFLICT (lock_key) DO NOTHING;

-- A reservation whose instant has arrived is consumed here, which is what makes admission once-only:
-- a second attempt of the same job books a fresh turn instead of reusing this one.
DELETE FROM {{schema}}.locks
WHERE
    lock_key = @p_lock_key || '.' || @p_job_id
    AND expires_at_utc <= {{now}};

-- Whether a reservation survived the statements above is the whole answer: none left means the turn
-- is now.
SELECT
    COALESCE(
        (SELECT r.expires_at_utc FROM {{schema}}.locks r WHERE r.lock_key = @p_lock_key || '.' || @p_job_id),
        {{now}}
    ) AS resume_at_utc,
    CASE
        WHEN EXISTS (SELECT 1 FROM {{schema}}.locks r WHERE r.lock_key = @p_lock_key || '.' || @p_job_id) THEN 0
        ELSE 1
    END AS admitted;
