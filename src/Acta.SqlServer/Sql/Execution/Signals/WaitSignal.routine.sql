/* Slot-locked arbiter for one durable wait: Set wins even past the due, an overdue Pending flips to
   Expired, Expired replays TimedOut forever. */
/* Arming is one-directional. A NULL due_at_utc is armed when the caller carries a timeout, so code
   redeployed with a bound can un-strand a wait suspended without one; a stored due is never
   overwritten, never extended, and never cleared by a later unbounded call. */
CREATE OR ALTER PROCEDURE {{schema}}.wait_signal
    @p_job_id BIGINT,
    @p_kind_code TINYINT,
    @p_name VARCHAR(128),
    @p_timeout_seconds INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @state TINYINT, @fmt TINYINT = 0;
    DECLARE @val VARBINARY(MAX) = NULL;
    DECLARE @due DATETIME2(3) = NULL;
    DECLARE @outcome SMALLINT;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT
            @state = status_code,
            @fmt = value_format_id,
            @val = value,
            @due = due_at_utc
        FROM {{schema}}.checkpoints WITH (UPDLOCK, HOLDLOCK)
        WHERE job_id = @p_job_id AND kind_code = @p_kind_code AND name = @p_name;

        IF @state = 20 /* JobCheckpointStatusCode.Set */
            BEGIN
                SET @outcome = 2 /* SignalWaitOutcomeCode.ContinueSet */;
            END
        ELSE IF @state = 30 /* JobCheckpointStatusCode.Expired */
            BEGIN
                SET @outcome = 3 /* SignalWaitOutcomeCode.TimedOut */;
                SET @fmt = 0 /* JobPayloadFormat.None */;
                SET @val = NULL;
            END
        ELSE IF @state = 10 /* JobCheckpointStatusCode.Pending */ AND @due IS NOT NULL AND @due <= @now
            BEGIN
                UPDATE {{schema}}.checkpoints
                SET
                    status_code = 30 /* JobCheckpointStatusCode.Expired */,
                    modified_at_utc = @now,
                    version = version + 1
                WHERE job_id = @p_job_id AND kind_code = @p_kind_code AND name = @p_name;

                SET @outcome = 3 /* SignalWaitOutcomeCode.TimedOut */;
                SET @fmt = 0 /* JobPayloadFormat.None */;
                SET @val = NULL;
            END
        ELSE
            BEGIN
                IF @state IS NULL
                    BEGIN
                        INSERT INTO {{schema}}.checkpoints (
                            job_id, kind_code, name, status_code, due_at_utc, value_format_id, value,
                            created_at_utc, modified_at_utc, version
                        )
                        VALUES (
                            @p_job_id, @p_kind_code, @p_name, 10 /* JobCheckpointStatusCode.Pending */,
                            CASE WHEN @p_timeout_seconds IS NULL THEN NULL ELSE DATEADD(SECOND, @p_timeout_seconds, @now) END,
                            0 /* JobPayloadFormat.None */, NULL, @now, @now, 0
                        );
                    END
                ELSE IF @state = 10 /* JobCheckpointStatusCode.Pending */ AND @due IS NULL AND @p_timeout_seconds IS NOT NULL
                    BEGIN
                        UPDATE {{schema}}.checkpoints
                        SET
                            due_at_utc = DATEADD(SECOND, @p_timeout_seconds, @now),
                            modified_at_utc = @now,
                            version = version + 1
                        WHERE
                            job_id = @p_job_id AND kind_code = @p_kind_code AND name = @p_name
                            AND status_code = 10 /* JobCheckpointStatusCode.Pending */
                            AND due_at_utc IS NULL;
                    END
                SET @outcome = 1 /* SignalWaitOutcomeCode.SuspendPending */;
                SET @fmt = 0 /* JobPayloadFormat.None */;
                SET @val = NULL;
            END

        COMMIT TRANSACTION;
        SELECT
            @outcome AS outcome_code,
            @fmt AS value_format_id,
            @val AS value;
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
