CREATE OR ALTER PROCEDURE {{schema}}.wait_signal
    @p_job_id    BIGINT,
    @p_kind_code TINYINT,
    @p_name      VARCHAR(128)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @state TINYINT, @fmt TINYINT = 0;
    DECLARE @val VARBINARY(MAX) = NULL;
    DECLARE @outcome SMALLINT;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @state = status_code, @fmt = value_format_id, @val = value
          FROM {{schema}}.checkpoints WITH (UPDLOCK, HOLDLOCK)
         WHERE job_id = @p_job_id AND kind_code = @p_kind_code AND name = @p_name;

        IF @state = 20 /* JobCheckpointStatusCode.Set */
        BEGIN
            SET @outcome = 2 /* SignalWaitOutcomeCode.ContinueSet */;
        END
        ELSE
        BEGIN
            IF @state IS NULL
            BEGIN
                INSERT INTO {{schema}}.checkpoints (
                    job_id, kind_code, name, status_code, value_format_id, value,
                    created_at_utc, modified_at_utc, version)
                VALUES (@p_job_id, @p_kind_code, @p_name, 10 /* JobCheckpointStatusCode.Pending */,
                        0 /* JobPayloadFormat.None */, NULL, @now, @now, 0);
            END
            SET @outcome = 1 /* SignalWaitOutcomeCode.SuspendPending */;
            SET @fmt = 0 /* JobPayloadFormat.None */;
            SET @val = NULL;
        END

        COMMIT TRANSACTION;
        SELECT @outcome AS outcome_code, @fmt AS value_format_id, @val AS value;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
        BEGIN
            ROLLBACK TRANSACTION;
        END;

        THROW;
    END CATCH;
END;
GO
