-- Version-CAS consume of an applied operator command: a miss means a newer command superseded the row
-- mid-apply and survives for the next tick. See IOutboxSignalStore.ConsumeAsync.
CREATE OR ALTER PROCEDURE {{schema}}.consume_outbox_signal
    @p_job_id BIGINT,
    @p_name VARCHAR(128),
    @p_expected_version INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        DELETE FROM {{schema}}.checkpoints
        WHERE
            job_id = @p_job_id
            AND kind_code = 20 /* JobCheckpointKindCode.Signal */
            AND name = @p_name
            AND version = @p_expected_version;

        SELECT CAST(@@ROWCOUNT AS BIGINT) AS consumed;

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
