-- GCRA with a reservation; the full contract is on ILockStore.ReserveRateAsync. A turn is honoured
-- only while it is fresh, at most one interval past its instant; one that went stale while executors
-- were busy goes back through the meter, so a queue of overdue jobs cannot all start at once.
CREATE OR ALTER PROCEDURE {{schema}}.reserve_rate
    @p_lock_key VARCHAR(256),
    @p_job_id BIGINT,
    @p_rate_interval_ms INT,
    @p_rate_burst INT,
    @p_grace_seconds INT,
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
        DECLARE @consumed TABLE (expires_at_utc DATETIME2(7));
        DECLARE @stored DATETIME2(7);
        DECLARE @due DATETIME2(7);
        DECLARE @turn DATETIME2(7);

        -- The bucket stores the arrival time plus the lookback, so the row expires exactly when an
        -- idle meter stops saying anything a missing one would not. A missing meter starts at now.
        IF NOT EXISTS (SELECT 1 FROM {{schema}}.locks WITH (UPDLOCK, HOLDLOCK) WHERE lock_key = @p_lock_key)
            INSERT INTO {{schema}}.locks (lock_key, job_id, expires_at_utc, hold_token)
            VALUES (@p_lock_key, @p_job_id, @now, @p_hold_token);

        -- Held for the rest of the call, so consume, hand back and allocate all decide against one
        -- serialized meter and no instant is ever handed to two jobs.
        SELECT @stored = b.expires_at_utc
        FROM {{schema}}.locks AS b WITH (UPDLOCK, HOLDLOCK)
        WHERE b.lock_key = @p_lock_key;

        -- The OUTPUT decides consumption, never a later absence: the sweep cannot slip between a read
        -- and the delete and give away a free admission. A stale turn is spent here too, then re-metered.
        DELETE FROM {{schema}}.locks
        OUTPUT DELETED.expires_at_utc INTO @consumed
        WHERE
            lock_key = @reservation
            AND expires_at_utc <= DATEADD(SECOND, @p_grace_seconds, @now);

        SELECT @due = DATEADD(SECOND, -@p_grace_seconds, c.expires_at_utc) FROM @consumed AS c;

        IF @due IS NOT NULL AND @due >= DATEADD(MILLISECOND, -@p_rate_interval_ms, @now)
            -- Back on time: the bucket counted this job when it allocated the turn, so charging it
            -- again would meter one job twice.
            SELECT @due AS resume_at_utc, CAST(1 AS INT) AS admitted;
        ELSE
            BEGIN
                IF @due IS NULL
                    -- A turn still ahead of now: the job is early, not owed a new instant.
                    SELECT @turn = DATEADD(SECOND, -@p_grace_seconds, r.expires_at_utc)
                    FROM {{schema}}.locks AS r
                    WHERE r.lock_key = @reservation;

                IF @turn IS NOT NULL
                    SELECT @turn AS resume_at_utc, CAST(0 AS INT) AS admitted;
                ELSE
                    BEGIN
                        IF @stored < @now
                            SET @stored = @now;
                        SET @turn = DATEADD(MILLISECOND, -@lookback_ms, @stored);

                        UPDATE {{schema}}.locks
                        SET
                            job_id = @p_job_id,
                            expires_at_utc = DATEADD(MILLISECOND, @p_rate_interval_ms, @stored),
                            hold_token = @p_hold_token
                        WHERE lock_key = @p_lock_key;

                        IF @turn <= @now
                            SELECT @turn AS resume_at_utc, CAST(1 AS INT) AS admitted;
                        ELSE
                            BEGIN
                                UPDATE {{schema}}.locks
                                SET
                                    job_id = @p_job_id,
                                    expires_at_utc = DATEADD(SECOND, @p_grace_seconds, @turn),
                                    hold_token = @p_hold_token
                                WHERE lock_key = @reservation;

                                IF @@ROWCOUNT = 0
                                    INSERT INTO {{schema}}.locks (lock_key, job_id, expires_at_utc, hold_token)
                                    VALUES (@reservation, @p_job_id, DATEADD(SECOND, @p_grace_seconds, @turn), @p_hold_token);

                                SELECT @turn AS resume_at_utc, CAST(0 AS INT) AS admitted;
                            END;
                    END;
            END;

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
