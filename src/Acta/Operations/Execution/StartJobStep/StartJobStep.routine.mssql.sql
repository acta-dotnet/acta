CREATE OR ALTER PROCEDURE {{schema}}.start_job_step
    @p_job_id BIGINT,
    @p_execution_number INT,
    @p_leased_by_worker_id INT,
    @p_step_name VARCHAR(128),
    @p_at_most_once BIT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        DECLARE @now DATETIME2(3) = SYSUTCDATETIME();
        DECLARE @outcome SMALLINT;
        DECLARE @version INT;
        DECLARE @rfid TINYINT = 0;
        DECLARE @result VARBINARY(MAX) = NULL;
        DECLARE @rmsg NVARCHAR(512) = NULL;
        DECLARE @state TINYINT;

        -- The step's own lock serializes starts of one step to commit (docs/sql-internals.md,
        -- "Step, checkpoint and rate locks"); a range lock on a step not yet stored would hold the gap every running job's next step
        -- falls in.
        DECLARE @step_lock NVARCHAR(255) = CONCAT(N'{{schema}}.step:', @p_job_id, N':', @p_step_name);
        DECLARE @step_lock_result INT;
        -- Positional: resource, mode, owner, timeout (-1 waits like any lock).
        EXEC @step_lock_result = sp_getapplock @step_lock, 'Exclusive', 'Transaction', -1;
        IF @step_lock_result < 0
            THROW 51205, 'ACTA:STEP_LOCK:The step lock was not granted (deadlock victim or cancelled).', 1;

        -- The step changes only while this attempt holds the job's row, update-locked to commit after the step lock
        -- (docs/sql-internals.md, "Execution fence").
        IF NOT EXISTS (
            SELECT 1
            FROM {{schema}}.jobs_state r WITH (UPDLOCK, ROWLOCK)
            WHERE
                r.job_id = @p_job_id
                AND r.job_status_code = 50 /* JobStatusCode.Executing */
                AND r.execution_number = @p_execution_number
                AND r.leased_by_worker_id = @p_leased_by_worker_id
        )
            THROW 50012, 'ACTA:EXECUTION_FENCED:start_job_step: the job is no longer executing this attempt.', 1;

        SELECT
            @state = step_status_code,
            @version = version,
            @rfid = result_format_id,
            @result = result,
            @rmsg = failure_message
        FROM {{schema}}.steps WITH (UPDLOCK)
        WHERE job_id = @p_job_id AND step_name = @p_step_name;

        IF @state IS NULL
            BEGIN
                INSERT INTO {{schema}}.steps (
                    job_id, step_name, step_status_code,
                    result_format_id, created_at_utc, modified_at_utc, version
                )
                VALUES (
                    @p_job_id, @p_step_name, 10 /* StepStatusCode.Pending */,
                    0 /* JobPayloadFormat.None */, @now, @now, 0
                );

                SET @outcome = 1 /* StartJobStepOutcomeCode.Invoke */;
                SET @version = 0;
                SET @rfid = 0 /* JobPayloadFormat.None */;
                SET @result = NULL;
                SET @rmsg = NULL;
            END
        ELSE IF @state = 100 /* StepStatusCode.Succeeded */
            BEGIN
                SET @outcome = 3 /* StartJobStepOutcomeCode.ReplaySuccess */;
                SET @rmsg = NULL;
            END
        ELSE IF @state = 200 /* StepStatusCode.Exhausted */
            BEGIN
                SET @outcome = 4 /* StartJobStepOutcomeCode.Exhausted */;
                SET @rfid = 0 /* JobPayloadFormat.None */;
                SET @result = NULL;
            END
        ELSE IF @state = 230 /* StepStatusCode.Interrupted */
            BEGIN
            -- Terminal at-most-once ambiguity from an earlier replay; re-throw consistently, no mutation.
                SET @outcome = 5 /* StartJobStepOutcomeCode.Interrupted */;
                SET @rfid = 0 /* JobPayloadFormat.None */;
                SET @result = NULL;
                SET @rmsg = NULL;
            END
        ELSE IF @p_at_most_once = 1
            BEGIN
            -- Pending step re-entered on replay under AtMostOnce: the worker died after start_job_step recorded
            -- the pending row but before complete_job_step. Do not re-invoke; terminalize the row Interrupted
            -- (one transition, one version bump) and let the orchestration throw StepInterruptedException.
                UPDATE {{schema}}.steps
                SET
                    step_status_code = 230 /* StepStatusCode.Interrupted */,
                    failure_message = N'At-most-once step re-entered before completion; outcome unknown.',
                    modified_at_utc = @now,
                    version = version + 1
                WHERE job_id = @p_job_id AND step_name = @p_step_name;

                SET @version = @version + 1;
                SET @outcome = 5 /* StartJobStepOutcomeCode.Interrupted */;
                SET @rfid = 0 /* JobPayloadFormat.None */;
                SET @result = NULL;
                SET @rmsg = NULL;
            END
        ELSE
            BEGIN
                -- An ordinary step with no success recorded runs again, its row unchanged.
                SET @outcome = 1 /* StartJobStepOutcomeCode.Invoke */;
                SET @rfid = 0 /* JobPayloadFormat.None */;
                SET @result = NULL;
                SET @rmsg = NULL;
            END

        SELECT
            @outcome AS outcome_code,
            @version AS version,
            @rfid AS result_format_id,
            @result AS result,
            @rmsg AS failure_message;

        IF @entry_trancount = 0
            COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @entry_trancount = 0 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
