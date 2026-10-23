CREATE OR ALTER PROCEDURE {{schema}}.complete_step
    @p_job_id BIGINT,
    @p_step_name VARCHAR(128),
    @p_succeeded BIT,
    @p_result_format_id TINYINT,
    @p_result VARBINARY(MAX),
    @p_failure_code TINYINT,
    @p_failure_message NVARCHAR(512),
    @p_delay_seconds INT,
    @p_max_attempts INT,
    @p_retry_window_seconds INT,
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
        DECLARE @attempt INT;
        DECLARE @created DATETIME2(3);
        DECLARE @next DATETIME2(3);
        DECLARE @outcome SMALLINT;

        SELECT
            @attempt = attempt_number,
            @created = created_at_utc
        FROM {{schema}}.steps
        WHERE job_id = @p_job_id AND step_name = @p_step_name;

        IF @p_succeeded = 1
            BEGIN
                UPDATE {{schema}}.steps
                SET
                    step_status_code = 100 /* StepStatusCode.Succeeded */,
                    result_format_id = @p_result_format_id,
                    result = @p_result,
                    next_retry_at_utc = NULL,
                    /* A success supersedes any earlier failed attempt, so the row must not keep that attempt's
                       failure snapshot: a Succeeded row with a failure_code reads as a failure to every post-mortem query. */
                    failure_code = NULL,
                    failure_message = NULL,
                    modified_at_utc = @now,
                    version = version + 1
                WHERE job_id = @p_job_id AND step_name = @p_step_name AND version = @p_version;

                IF @@ROWCOUNT = 0
                    SET @outcome = 4 /* CompleteStepOutcomeCode.StaleVersion */;
                ELSE
                    SET @outcome = 1 /* CompleteStepOutcomeCode.Succeeded */;
                SET @next = NULL;
            END
        ELSE
            BEGIN
                SET @next = DATEADD(SECOND, @p_delay_seconds, @now);

                IF
                    @attempt >= @p_max_attempts
                    OR (
                        @p_retry_window_seconds IS NOT NULL
                        AND @next > DATEADD(SECOND, @p_retry_window_seconds, @created)
                    )
                    BEGIN
                        UPDATE {{schema}}.steps
                        SET
                            step_status_code = 200 /* StepStatusCode.Exhausted */,
                            next_retry_at_utc = NULL,
                            failure_code = @p_failure_code,
                            failure_message = @p_failure_message,
                            modified_at_utc = @now,
                            version = version + 1
                        WHERE job_id = @p_job_id AND step_name = @p_step_name AND version = @p_version;

                        IF @@ROWCOUNT = 0
                            SET @outcome = 4 /* CompleteStepOutcomeCode.StaleVersion */;
                        ELSE
                            SET @outcome = 3 /* CompleteStepOutcomeCode.Exhausted */;
                        SET @next = NULL;
                    END
                ELSE
                    BEGIN
                        UPDATE {{schema}}.steps
                        SET
                            next_retry_at_utc = @next,
                            failure_code = @p_failure_code,
                            failure_message = @p_failure_message,
                            modified_at_utc = @now,
                            version = version + 1
                        WHERE job_id = @p_job_id AND step_name = @p_step_name AND version = @p_version;

                        IF @@ROWCOUNT = 0
                            BEGIN
                                SET @outcome = 4 /* CompleteStepOutcomeCode.StaleVersion */;
                                SET @next = NULL;
                            END
                        ELSE
                            SET @outcome = 2 /* CompleteStepOutcomeCode.RetryScheduled */;
                    END
            END

        SELECT
            @outcome AS outcome_code,
            @next AS next_retry_at_utc;

        IF @entry_trancount = 0
            COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @entry_trancount = 0 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
