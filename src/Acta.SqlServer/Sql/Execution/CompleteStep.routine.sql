CREATE OR ALTER PROCEDURE {{schema}}.complete_step
    @p_job_id BIGINT,
    @p_name VARCHAR(128),
    @p_succeeded BIT,
    @p_result_format_id TINYINT,
    @p_result VARBINARY(MAX),
    @p_reason_code TINYINT,
    @p_reason_message NVARCHAR(512),
    @p_delay_seconds INT,
    @p_max_attempts SMALLINT,
    @p_retry_window_seconds INT,
    @p_version INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @attempt SMALLINT;
    DECLARE @created DATETIME2(3);
    DECLARE @next DATETIME2(3);
    DECLARE @outcome SMALLINT;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT
            @attempt = attempt_number,
            @created = created_at_utc
        FROM {{schema}}.steps
        WHERE job_id = @p_job_id AND name = @p_name;

        IF @p_succeeded = 1
            BEGIN
                UPDATE {{schema}}.steps
                SET
                    status_code = 100 /* JobStepStatusCode.Succeeded */,
                    result_format_id = @p_result_format_id,
                    result = @p_result,
                    next_retry_at_utc = NULL,
                    modified_at_utc = @now,
                    version = version + 1
                WHERE job_id = @p_job_id AND name = @p_name AND version = @p_version;

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
                            status_code = 200 /* JobStepStatusCode.Exhausted */,
                            next_retry_at_utc = NULL,
                            reason_code = @p_reason_code,
                            reason_message = @p_reason_message,
                            modified_at_utc = @now,
                            version = version + 1
                        WHERE job_id = @p_job_id AND name = @p_name AND version = @p_version;

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
                            reason_code = @p_reason_code,
                            reason_message = @p_reason_message,
                            modified_at_utc = @now,
                            version = version + 1
                        WHERE job_id = @p_job_id AND name = @p_name AND version = @p_version;

                        IF @@ROWCOUNT = 0
                            BEGIN
                                SET @outcome = 4 /* CompleteStepOutcomeCode.StaleVersion */;
                                SET @next = NULL;
                            END
                        ELSE
                            SET @outcome = 2 /* CompleteStepOutcomeCode.RetryScheduled */;
                    END
            END

        COMMIT TRANSACTION;

        SELECT
            @outcome AS outcome_code,
            @next AS next_retry_at_utc;
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
