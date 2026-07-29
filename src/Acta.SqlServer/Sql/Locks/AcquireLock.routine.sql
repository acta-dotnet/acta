CREATE OR ALTER PROCEDURE {{schema}}.acquire_lock
    @p_lease_key         VARCHAR(256),
    @p_job_id            BIGINT,
    @p_lease_ttl_seconds INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @out TABLE (version INT);

    /* Steal-or-insert without MERGE HOLDLOCK: the point UPDATE takes only a key lock, and the
       insert race for a brand-new key is settled by the primary key (a loser lands on 2627/2601
       and reports not-acquired) instead of serializable range locks that deadlock same-gap inserts. */
    UPDATE {{schema}}.leases
       SET job_id          = @p_job_id,
           expires_at_utc  = DATEADD(SECOND, @p_lease_ttl_seconds, @now),
           version         = version + 1
    OUTPUT inserted.version INTO @out
     WHERE lease_key = @p_lease_key
       AND expires_at_utc <= @now;

    IF @@ROWCOUNT = 0 AND NOT EXISTS (SELECT 1 FROM {{schema}}.leases WHERE lease_key = @p_lease_key)
    BEGIN
        BEGIN TRY
            INSERT INTO {{schema}}.leases (lease_key, kind_code, job_id, expires_at_utc, version)
            OUTPUT inserted.version INTO @out
            VALUES (@p_lease_key, 10 /* LeaseKindCode.Lock */, @p_job_id, DATEADD(SECOND, @p_lease_ttl_seconds, @now), 1);
        END TRY
        BEGIN CATCH
            IF ERROR_NUMBER() NOT IN (2627, 2601)
            BEGIN
                THROW;
            END;
        END CATCH;
    END;

    SELECT version FROM @out;
END;
GO
