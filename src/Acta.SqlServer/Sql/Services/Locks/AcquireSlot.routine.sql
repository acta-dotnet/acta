CREATE OR ALTER PROCEDURE {{schema}}.acquire_slot
    @p_lock_key_prefix VARCHAR(256),
    @p_slot_count INT,
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
        DECLARE @out TABLE (lock_key VARCHAR(256));
        DECLARE @slot_key VARCHAR(256);

        -- Lowest-numbered free slot: no row, or a row whose hold expired. MAXRECURSION 0 because the
        -- limit reaches 1024, well past the default recursion ceiling of 100.
        WITH slots (i) AS (
            SELECT 0
            UNION ALL
            SELECT i + 1 FROM slots WHERE i + 1 < @p_slot_count
        )
        SELECT TOP (1) @slot_key = @p_lock_key_prefix + '.' + CAST(slots.i AS VARCHAR(10))
        FROM slots
        WHERE NOT EXISTS (
            SELECT 1
            FROM {{schema}}.locks held
            WHERE
                held.lock_key = @p_lock_key_prefix + '.' + CAST(slots.i AS VARCHAR(10))
                AND held.expires_at_utc > @now
        )
        ORDER BY slots.i
        OPTION (MAXRECURSION 0);

        -- Point update, then insert on a miss, exactly as acquire_lock does: the primary key
        -- arbitrates new-key races, and serializable gap locking would serialize unrelated slots.
        IF @slot_key IS NOT NULL
            BEGIN
                UPDATE {{schema}}.locks
                SET
                    job_id = @p_job_id,
                    expires_at_utc = DATEADD(SECOND, @p_lease_ttl_seconds, @now),
                    hold_token = @p_hold_token
                OUTPUT INSERTED.lock_key INTO @out
                WHERE
                    lock_key = @slot_key
                    AND expires_at_utc <= @now;

                IF @@ROWCOUNT = 0 AND NOT EXISTS (
                    SELECT 1 FROM {{schema}}.locks
                    WHERE lock_key = @slot_key
                )
                    BEGIN
                        INSERT INTO {{schema}}.locks (lock_key, job_id, expires_at_utc, hold_token)
                        OUTPUT INSERTED.lock_key INTO @out
                        VALUES (@slot_key, @p_job_id, DATEADD(SECOND, @p_lease_ttl_seconds, @now), @p_hold_token);
                    END;
            END;

        SELECT lock_key FROM @out;

        IF @entry_trancount = 0
            COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @entry_trancount = 0 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;

        -- A duplicate-key loser raced another admission onto the same slot and acquired nothing; the
        -- caller settles that as a plain miss. XACT_ABORT requires rollback before returning it.
        IF @entry_trancount = 0 AND ERROR_NUMBER() IN (2627, 2601)
            SELECT CAST(NULL AS VARCHAR(256)) AS lock_key WHERE 1 = 0;
        ELSE
            THROW;
    END CATCH;
END;
GO
