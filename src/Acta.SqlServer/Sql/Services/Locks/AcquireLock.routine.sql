CREATE OR ALTER PROCEDURE {{schema}}.acquire_lock
    @p_lock_key VARCHAR(256),
    @p_job_id BIGINT,
    @p_lease_ttl_seconds INT,
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
        DECLARE @out TABLE (hold_token UNIQUEIDENTIFIER);

        -- Point update, then insert on a miss. The primary key arbitrates new-key races;
        -- serializable gap locking would serialize unrelated lock keys in the same index gap.
        UPDATE {{schema}}.locks
        SET
            job_id = @p_job_id,
            expires_at_utc = DATEADD(SECOND, @p_lease_ttl_seconds, @now),
            hold_token = @p_hold_token
        OUTPUT INSERTED.hold_token INTO @out
        WHERE
            lock_key = @p_lock_key
            AND expires_at_utc <= @now;

        IF @@ROWCOUNT = 0 AND NOT EXISTS (
            SELECT 1 FROM {{schema}}.locks
            WHERE lock_key = @p_lock_key
        )
            BEGIN
                INSERT INTO {{schema}}.locks (lock_key, job_id, expires_at_utc, hold_token)
                OUTPUT INSERTED.hold_token INTO @out
                VALUES (@p_lock_key, @p_job_id, DATEADD(SECOND, @p_lease_ttl_seconds, @now), @p_hold_token);
            END;

        SELECT hold_token FROM @out;

        IF @entry_trancount = 0
            COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @entry_trancount = 0 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;

        -- A duplicate-key loser acquired nothing. XACT_ABORT requires rollback before returning
        -- that ordinary miss; an outer transaction's owner must handle its own aborted transaction.
        IF @entry_trancount = 0 AND ERROR_NUMBER() IN (2627, 2601)
            SELECT CAST(NULL AS UNIQUEIDENTIFIER) AS hold_token WHERE 1 = 0;
        ELSE
            THROW;
    END CATCH;
END;
GO
