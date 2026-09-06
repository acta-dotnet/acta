CREATE OR ALTER PROCEDURE {{schema}}.update_alert_delivery
    @p_id BIGINT,
    @p_version INT,
    @p_delivery_status_code TINYINT,
    @p_retry_count TINYINT,
    @p_retry_after_utc DATETIME2(7)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        /* Compare-and-swap on the version the delivery read handed out: an attempt whose row moved while the
           send was in flight - resolved by an operator, or settled by a competing worker - writes nothing and
           returns no row, and the caller treats the empty result as "the newer state stands". */
        UPDATE {{schema}}.alerts
        SET
            delivery_status_code = @p_delivery_status_code,
            retry_count = @p_retry_count,
            retry_after_utc = @p_retry_after_utc,
            modified_at_utc = SYSUTCDATETIME(),
            version = version + 1
        OUTPUT INSERTED.id
        WHERE
            id = @p_id
            AND version = @p_version;

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
