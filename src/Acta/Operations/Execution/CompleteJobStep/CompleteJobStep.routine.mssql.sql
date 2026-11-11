CREATE OR ALTER PROCEDURE {{schema}}.complete_job_step
    @p_job_id BIGINT,
    @p_execution_number BIGINT,
    @p_leased_by_worker_id INT,
    @p_step_name VARCHAR(128),
    @p_succeeded BIT,
    @p_result_format_id TINYINT,
    @p_result VARBINARY(MAX),
    @p_failure_message NVARCHAR(512),
    @p_version INT
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

        -- The step changes only while this attempt holds the job's row, update-locked to commit before the step row
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
            THROW 50012, 'ACTA:EXECUTION_FENCED:complete_job_step: the job is no longer executing this attempt.', 1;

        -- Only an at-most-once body records a failure, and the failure ends the step Failed.
        UPDATE {{schema}}.steps
        SET
            step_status_code = CASE WHEN @p_succeeded = 1 THEN 100 /* StepStatusCode.Succeeded */ ELSE 200 /* StepStatusCode.Failed */ END,
            result_format_id = @p_result_format_id,
            result = @p_result,
            failure_message = @p_failure_message,
            modified_at_utc = @now,
            version = version + 1
        WHERE job_id = @p_job_id AND step_name = @p_step_name AND version = @p_version;

        IF @@ROWCOUNT = 0
            SET @outcome = 4 /* CompleteJobStepOutcomeCode.StaleVersion */;
        ELSE IF @p_succeeded = 1
            SET @outcome = 1 /* CompleteJobStepOutcomeCode.Succeeded */;
        ELSE
            SET @outcome = 3 /* CompleteJobStepOutcomeCode.Failed */;

        SELECT @outcome AS outcome_code;

        IF @entry_trancount = 0
            COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @entry_trancount = 0 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
