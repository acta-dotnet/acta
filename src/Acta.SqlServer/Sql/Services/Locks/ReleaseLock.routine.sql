CREATE OR ALTER PROCEDURE {{schema}}.release_lock
    @p_lock_key VARCHAR(256),
    @p_hold_token UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        DELETE FROM {{schema}}.locks
        OUTPUT DELETED.hold_token
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
