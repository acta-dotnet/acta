CREATE OR ALTER PROCEDURE {{schema}}.start_step
    @p_job_id       BIGINT,
    @p_name         VARCHAR(128),
    @p_at_most_once BIT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @outcome SMALLINT;
    DECLARE @attempt SMALLINT;
    DECLARE @version INT;
    DECLARE @next DATETIME2(3) = NULL;
    DECLARE @rfid TINYINT = 0;
    DECLARE @result VARBINARY(MAX) = NULL;
    DECLARE @rcode SMALLINT = NULL;
    DECLARE @rmsg NVARCHAR(512) = NULL;
    DECLARE @state TINYINT;
    DECLARE @existing_next DATETIME2(3);

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @state         = state_code,
               @attempt       = attempt_number,
               @version       = version,
               @existing_next = next_retry_at_utc,
               @rfid          = result_format_id,
               @result        = result,
               @rcode         = reason_code,
               @rmsg          = reason_message
          FROM {{schema}}.steps WITH (UPDLOCK, HOLDLOCK, INDEX(ux_steps_job_name))
         WHERE job_id = @p_job_id AND name = @p_name;

        IF @state IS NULL
        BEGIN
            INSERT INTO {{schema}}.steps (
                job_id, name, state_code, attempt_number,
                result_format_id, created_at_utc, modified_at_utc, version)
            VALUES (
                @p_job_id, @p_name, 10 /* JobStepStateCode.Pending */, 1,
                0 /* JobPayloadFormat.None */, @now, @now, 0);

            SET @outcome = 1 /* StartStepOutcomeCode.Invoke */;
            SET @attempt = 1;
            SET @version = 0;
            SET @rfid = 0 /* JobPayloadFormat.None */;
            SET @result = NULL;
            SET @rcode = NULL;
            SET @rmsg = NULL;
            SET @next = NULL;
        END
        ELSE IF @state = 100 /* JobStepStateCode.Succeeded */
        BEGIN
            SET @outcome = 3 /* StartStepOutcomeCode.ReplaySuccess */;
            SET @next = NULL;
            SET @rcode = NULL;
            SET @rmsg = NULL;
        END
        ELSE IF @state = 200 /* JobStepStateCode.Exhausted */
        BEGIN
            SET @outcome = 4 /* StartStepOutcomeCode.Exhausted */;
            SET @next = NULL;
            SET @rfid = 0 /* JobPayloadFormat.None */;
            SET @result = NULL;
        END
        ELSE IF @state = 230 /* JobStepStateCode.Interrupted */
        BEGIN
            -- Terminal at-most-once ambiguity from an earlier replay; re-throw consistently, no mutation.
            SET @outcome = 5 /* StartStepOutcomeCode.Interrupted */;
            SET @next = NULL;
            SET @rfid = 0 /* JobPayloadFormat.None */;
            SET @result = NULL;
            SET @rcode = NULL;
            SET @rmsg = NULL;
        END
        ELSE IF @existing_next IS NOT NULL AND @existing_next > @now
        BEGIN
            SET @outcome = 2 /* StartStepOutcomeCode.Suspend */;
            SET @next = @existing_next;
            SET @rfid = 0 /* JobPayloadFormat.None */;
            SET @result = NULL;
            SET @rcode = NULL;
            SET @rmsg = NULL;
        END
        ELSE IF @p_at_most_once = 1
        BEGIN
            -- Pending slot re-entered on replay under AtMostOnce: the worker died after start_step recorded
            -- the pending row but before complete_step. Do not re-invoke; terminalize the row Interrupted
            -- (one transition, one version bump) and let the orchestration throw StepInterruptedException.
            UPDATE {{schema}}.steps
               SET state_code      = 230 /* JobStepStateCode.Interrupted */,
                   reason_code     = 63 /* JobEventReasonCode.JobStepInterrupted */,
                   reason_message  = N'At-most-once step re-entered before completion; outcome unknown.',
                   modified_at_utc = @now,
                   version         = version + 1
             WHERE job_id = @p_job_id AND name = @p_name;

            SET @version  = @version + 1;
            SET @outcome  = 5 /* StartStepOutcomeCode.Interrupted */;
            SET @next = NULL;
            SET @rfid = 0 /* JobPayloadFormat.None */;
            SET @result = NULL;
            SET @rcode = NULL;
            SET @rmsg = NULL;
        END
        ELSE
        BEGIN
            UPDATE {{schema}}.steps
               SET attempt_number  = attempt_number + 1,
                   modified_at_utc = @now,
                   version         = version + 1
             WHERE job_id = @p_job_id AND name = @p_name;

            SET @attempt  = @attempt + 1;
            SET @version  = @version + 1;
            SET @outcome  = 1 /* StartStepOutcomeCode.Invoke */;
            SET @next = NULL;
            SET @rfid = 0 /* JobPayloadFormat.None */;
            SET @result = NULL;
            SET @rcode = NULL;
            SET @rmsg = NULL;
        END

        COMMIT TRANSACTION;

        SELECT @outcome AS outcome_code, @attempt AS attempt_number, @version AS version,
               @next AS next_retry_at_utc, @rfid AS result_format_id, @result AS result,
               @rcode AS reason_code, @rmsg AS reason_message;
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
