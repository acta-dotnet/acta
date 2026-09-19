-- GCRA with a reservation; the contract is on ILockStore.ReserveRateAsync. Locking: UPDLOCK/HOLDLOCK
-- over the bucket's equality predicate serializes two requesters and covers the missing-row case,
-- where the range lock is what stops both inserting a first bucket. Same idiom as the alert dedupe.
CREATE OR ALTER PROCEDURE {{schema}}.reserve_rate
    @p_lock_key VARCHAR(256),
    @p_job_id BIGINT,
    @p_rate_interval_ms INT,
    @p_rate_burst INT,
    @p_hold_token UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
        DECLARE @reservation VARCHAR(256) = @p_lock_key + '.' + CAST(@p_job_id AS VARCHAR(20));
        -- How far behind now an idle meter is allowed to be, which is what hands out the burst: one
        -- interval short of a whole period, so the burst-th request lands on now and the next waits.
        DECLARE @lookback_ms INT = (@p_rate_burst - 1) * @p_rate_interval_ms;
        DECLARE @stored DATETIME2(7);
        DECLARE @turn DATETIME2(7);

        -- The bucket stores the arrival time plus the lookback, so the row expires exactly when it
        -- stops saying anything a missing row would not. Skipped for a job that already holds a
        -- reservation: that request moved the bucket when it booked, so its turn is paid for.
        IF NOT EXISTS (SELECT 1 FROM {{schema}}.locks WHERE lock_key = @reservation)
            BEGIN
                SELECT @stored = l.expires_at_utc
                FROM {{schema}}.locks AS l WITH (UPDLOCK, HOLDLOCK)
                WHERE l.lock_key = @p_lock_key;

                SET @stored = CASE WHEN @stored > @now THEN @stored ELSE @now END;
                SET @turn = DATEADD(MILLISECOND, -@lookback_ms, @stored);

                UPDATE {{schema}}.locks
                SET
                    job_id = @p_job_id,
                    expires_at_utc = DATEADD(MILLISECOND, @p_rate_interval_ms, @stored),
                    hold_token = @p_hold_token
                WHERE lock_key = @p_lock_key;

                IF @@ROWCOUNT = 0
                    INSERT INTO {{schema}}.locks (lock_key, job_id, expires_at_utc, hold_token)
                    VALUES (@p_lock_key, @p_job_id, DATEADD(MILLISECOND, @p_rate_interval_ms, @stored), @p_hold_token);

                -- Book the arrival time when it is still ahead of now, so the caller re-arms once
                -- instead of racing the meter again.
                IF @turn > @now
                    INSERT INTO {{schema}}.locks (lock_key, job_id, expires_at_utc, hold_token)
                    VALUES (@reservation, @p_job_id, @turn, @p_hold_token);
            END;

        -- A reservation whose instant has arrived is consumed here, which is what makes admission
        -- once-only: a second attempt of the same job books a fresh turn instead of reusing this one.
        DELETE FROM {{schema}}.locks
        WHERE
            lock_key = @reservation
            AND expires_at_utc <= @now;

        -- Whether a reservation survived the statements above is the whole answer: none left means
        -- the turn is now.
        SELECT
            COALESCE((SELECT r.expires_at_utc FROM {{schema}}.locks r WHERE r.lock_key = @reservation), @now) AS resume_at_utc,
            CASE WHEN EXISTS (SELECT 1 FROM {{schema}}.locks r WHERE r.lock_key = @reservation) THEN 0 ELSE 1 END AS admitted;

        IF @entry_trancount = 0
            COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @entry_trancount = 0 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;

        THROW;
    END CATCH;
END;
GO
