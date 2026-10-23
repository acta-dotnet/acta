CREATE OR ALTER PROCEDURE {{schema}}.arm_or_consume_sleep_timer
    @p_job_id BIGINT,
    @p_execution_number INT,
    @p_leased_by_worker_id INT,
    @p_checkpoint_name VARCHAR(128),
    @p_delay_seconds INT = NULL,
    @p_resume_at_utc DATETIME2(3) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        DECLARE @now DATETIME2(3) = SYSUTCDATETIME();
        DECLARE @due DATETIME2(3) = COALESCE(@p_resume_at_utc, DATEADD(SECOND, @p_delay_seconds, @now));
        DECLARE @outcome SMALLINT;
        DECLARE @result_due DATETIME2(3) = NULL;
        DECLARE @existing_state TINYINT;
        DECLARE @existing_due DATETIME2(3);
        DECLARE @lock_id BIGINT;

        -- The job's row is the timer's job-level mutex, and the execution fence: the timer changes only while this
        -- attempt holds it (docs/sql-internals.md, "Execution fence").
        SELECT @lock_id = job_id
        FROM {{schema}}.jobs_state WITH (UPDLOCK, ROWLOCK)
        WHERE
            job_id = @p_job_id
            AND job_status_code = 50 /* JobStatusCode.Executing */
            AND execution_number = @p_execution_number
            AND leased_by_worker_id = @p_leased_by_worker_id;

        IF @lock_id IS NULL
            THROW 50012, 'ACTA:EXECUTION_FENCED:arm_or_consume_sleep_timer: the job is no longer executing this attempt.', 1;

        SELECT
            @existing_state = checkpoint_status_code,
            @existing_due = due_at_utc
        FROM {{schema}}.checkpoints
        WHERE
            job_id = @p_job_id
            AND checkpoint_kind_code = 30 /* CheckpointKindCode.Timer */
            AND checkpoint_name = @p_checkpoint_name;

        IF @existing_state = 10 /* CheckpointStatusCode.Pending */ AND @existing_due > @now
            BEGIN

                SET @outcome = 1 /* ArmOrConsumeSleepTimerOutcomeCode.Suspend */;
                SET @result_due = @existing_due;
            END
        ELSE IF @existing_state = 10 /* CheckpointStatusCode.Pending */
            BEGIN

                UPDATE {{schema}}.checkpoints
                SET
                    checkpoint_status_code = 100 /* CheckpointStatusCode.Consumed */,
                    modified_at_utc = @now,
                    version = version + 1
                WHERE job_id = @p_job_id AND checkpoint_kind_code = 30 /* CheckpointKindCode.Timer */ AND checkpoint_name = @p_checkpoint_name;

                /* Only the worker still executing may clear the instant. A reclaimed row is Ready again
                   on an instant the reclaim owns, and ck_jobs_state_ready_due requires Ready to keep one. */
                UPDATE {{schema}}.jobs_state
                SET
                    next_run_at_utc = NULL,
                    modified_at_utc = @now,
                    version = version + 1
                WHERE job_id = @p_job_id AND job_status_code = 50 /* JobStatusCode.Executing */;

                SET @outcome = 2 /* ArmOrConsumeSleepTimerOutcomeCode.Continue */;
            END
        ELSE IF @existing_state IS NOT NULL
            BEGIN

                SET @outcome = 2 /* ArmOrConsumeSleepTimerOutcomeCode.Continue */;
            END
        ELSE IF @due <= @now
            BEGIN

                SET @outcome = 2 /* ArmOrConsumeSleepTimerOutcomeCode.Continue */;
            END
        ELSE IF
            EXISTS (
                SELECT 1 FROM {{schema}}.checkpoints
                WHERE
                    job_id = @p_job_id AND checkpoint_kind_code = 30 /* CheckpointKindCode.Timer */ AND checkpoint_status_code = 10
            /* CheckpointStatusCode.Pending */
            )
            BEGIN
                SET @outcome = 3 /* ArmOrConsumeSleepTimerOutcomeCode.Reject */;
            END
        ELSE
            BEGIN
                INSERT INTO {{schema}}.checkpoints (
                    job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code, due_at_utc,
                    created_at_utc, modified_at_utc, version
                )
                VALUES (
                    @p_job_id, 30 /* CheckpointKindCode.Timer */, @p_checkpoint_name, 10 /* CheckpointStatusCode.Pending */, @due,
                    @now, @now, 0
                );

                SET @outcome = 1 /* ArmOrConsumeSleepTimerOutcomeCode.Suspend */;
                SET @result_due = @due;
            END

        SELECT
            @outcome AS outcome_code,
            @result_due AS due_at_utc;

        IF @entry_trancount = 0
            COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @entry_trancount = 0 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
