CREATE OR ALTER PROCEDURE {{schema}}.acquire_lock
    @p_lock_key VARCHAR(256),
    @p_job_id BIGINT,
    @p_lease_ttl_seconds INT,
    @p_hold_token UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @out TABLE (hold_token UNIQUEIDENTIFIER);

    /* Steal-or-insert without MERGE HOLDLOCK: the point UPDATE takes only a key lock, and the
       insert race for a brand-new key is settled by the primary key (a loser lands on 2627/2601
       and reports not-acquired) instead of serializable range locks that deadlock same-gap inserts. */
    UPDATE {{schema}}.locks
    SET
        job_id = @p_job_id,
        expires_at_utc = DATEADD(SECOND, @p_lease_ttl_seconds, @now),
        hold_token = @p_hold_token
    OUTPUT INSERTED.hold_token INTO @out
    WHERE
        lock_key = @p_lock_key
        AND expires_at_utc <= @now;

    IF
        @@ROWCOUNT = 0 AND NOT EXISTS (
            SELECT 1 FROM {{schema}}.locks
            WHERE lock_key = @p_lock_key
        )
        BEGIN
            BEGIN TRY
                INSERT INTO {{schema}}.locks (lock_key, job_id, expires_at_utc, hold_token)
                OUTPUT INSERTED.hold_token INTO @out
                VALUES (@p_lock_key, @p_job_id, DATEADD(SECOND, @p_lease_ttl_seconds, @now), @p_hold_token);
            END TRY
            BEGIN CATCH
                IF ERROR_NUMBER() NOT IN (2627, 2601)
                    BEGIN
                        THROW;
                    END;
            END CATCH;
        END;

    SELECT hold_token FROM @out;
END;
GO
