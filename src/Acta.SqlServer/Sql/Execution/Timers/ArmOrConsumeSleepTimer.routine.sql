CREATE OR ALTER PROCEDURE {{schema}}.arm_or_consume_sleep_timer
    @p_job_id        BIGINT,
    @p_name          VARCHAR(128),
    @p_delay_seconds INT = NULL,
    @p_resume_at_utc DATETIME2(3) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @due DATETIME2(3) = COALESCE(@p_resume_at_utc, DATEADD(SECOND, @p_delay_seconds, @now));
    DECLARE @outcome SMALLINT;
    DECLARE @result_due DATETIME2(3) = NULL;
    DECLARE @existing_state TINYINT;
    DECLARE @existing_due DATETIME2(3);
    DECLARE @lock_id BIGINT;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @lock_id = job_id
          FROM {{schema}}.runtimes WITH (UPDLOCK, HOLDLOCK)
         WHERE job_id = @p_job_id;

        SELECT @existing_state = state_code,
               @existing_due   = due_at_utc
          FROM {{schema}}.checkpoints
         WHERE job_id = @p_job_id
           AND kind_code = 30 /* JobCheckpointKindCode.Timer */
           AND name   = @p_name;

        IF @existing_state = 10 /* JobCheckpointStateCode.Pending */ AND @existing_due > @now
        BEGIN

            SET @outcome = 1 /* SleepOutcome.Suspend */;
            SET @result_due = @existing_due;
        END
        ELSE IF @existing_state = 10 /* JobCheckpointStateCode.Pending */
        BEGIN

            UPDATE {{schema}}.checkpoints
               SET state_code      = 100 /* JobCheckpointStateCode.Consumed */,
                   modified_at_utc = @now,
                   version         = version + 1
             WHERE job_id = @p_job_id AND kind_code = 30 /* JobCheckpointKindCode.Timer */ AND name = @p_name;

            UPDATE {{schema}}.runtimes
               SET next_run_at_utc = NULL,
                   modified_at_utc = @now,
                   version         = version + 1
             WHERE job_id = @p_job_id;

            SET @outcome = 2 /* SleepOutcome.Continue */;
        END
        ELSE IF @existing_state IS NOT NULL
        BEGIN

            SET @outcome = 2 /* SleepOutcome.Continue */;
        END
        ELSE IF @due <= @now
        BEGIN

            SET @outcome = 2 /* SleepOutcome.Continue */;
        END
        ELSE IF EXISTS (
            SELECT 1 FROM {{schema}}.checkpoints
             WHERE job_id = @p_job_id AND kind_code = 30 /* JobCheckpointKindCode.Timer */ AND state_code = 10 /* JobCheckpointStateCode.Pending */)
        BEGIN
            SET @outcome = 3 /* SleepOutcome.Reject */;
        END
        ELSE
        BEGIN
            INSERT INTO {{schema}}.checkpoints (
                job_id, kind_code, name, state_code, due_at_utc,
                created_at_utc, modified_at_utc, version)
            VALUES (
                @p_job_id, 30 /* JobCheckpointKindCode.Timer */, @p_name, 10 /* JobCheckpointStateCode.Pending */, @due,
                @now, @now, 0);

            SET @outcome = 1 /* SleepOutcome.Suspend */;
            SET @result_due = @due;
        END

        COMMIT TRANSACTION;

        SELECT @outcome AS outcome_code, @result_due AS due_at_utc;
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
