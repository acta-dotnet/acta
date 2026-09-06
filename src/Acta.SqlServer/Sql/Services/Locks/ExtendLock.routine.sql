CREATE OR ALTER PROCEDURE {{schema}}.extend_lock
    @p_lock_key VARCHAR(256),
    @p_hold_token UNIQUEIDENTIFIER,
    @p_lease_ttl_seconds INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        UPDATE {{schema}}.locks
        SET expires_at_utc = DATEADD(SECOND, @p_lease_ttl_seconds, SYSUTCDATETIME())
        OUTPUT INSERTED.hold_token
        WHERE lock_key = @p_lock_key AND hold_token = @p_hold_token;

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
