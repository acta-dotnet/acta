/* Slot-locked arbiter for one durable wait: Set wins even past the due, an overdue Pending flips to
   Expired, Expired replays TimedOut forever. */
/* Arming is one-directional. A NULL due_at_utc is armed when the caller carries a bound (a length, or an instant: a
   group's stored deadline), so code redeployed with a bound can un-strand a wait suspended without one; a stored due
   is never overwritten, never extended, and never cleared by a subsequent unbounded call. */
/* The read below needs no re-read after a lost insert the way pg's does: the slot's own lock, taken
   first (docs/sql-internals.md, "Slot locks"), makes a concurrent first arrival wait
   here and then read the winner's row, arriving at the same resolution instead of racing the insert. */
/* A wait this call armed with a length suspends once: the length is positive and the arm is the last
   act of its branch. A slot this call arms at or past an instant is armed Expired, so a child landing
   after it cannot revive the wait, and the wait times out now. Pg states the same rules. */
CREATE OR ALTER PROCEDURE {{schema}}.wait_signal
    @p_job_id BIGINT,
    @p_execution_number INT,
    @p_leased_by_worker_id INT,
    @p_checkpoint_kind_code TINYINT,
    @p_checkpoint_name VARCHAR(128),
    @p_timeout_seconds INT = NULL,
    @p_due_at_utc DATETIME2(3) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        DECLARE @now DATETIME2(3) = SYSUTCDATETIME();
        DECLARE @state TINYINT, @fmt TINYINT = 0;
        DECLARE @val VARBINARY(MAX) = NULL;
        DECLARE @due DATETIME2(3) = NULL;
        DECLARE @outcome SMALLINT;
        DECLARE @arm_due DATETIME2(3) = COALESCE(@p_due_at_utc, DATEADD(SECOND, @p_timeout_seconds, @now));
        DECLARE @arm_state TINYINT = CASE
            WHEN @arm_due <= @now THEN 30 /* CheckpointStatusCode.Expired */
            ELSE 10 /* CheckpointStatusCode.Pending */
        END;

        DECLARE @slot_lock NVARCHAR(255) = CONCAT(N'{{schema}}.slot:', @p_job_id, N':', @p_checkpoint_kind_code, N':', @p_checkpoint_name);
        DECLARE @slot_lock_result INT;
        -- Positional: resource, mode, owner, timeout (-1 waits like any lock).
        EXEC @slot_lock_result = sp_getapplock @slot_lock, 'Exclusive', 'Transaction', -1;
        IF @slot_lock_result < 0
            THROW 51205, 'ACTA:SLOT_LOCK:The checkpoint slot lock was not granted (deadlock victim or cancelled).', 1;

        -- The wait changes the slot only while this attempt holds the job's row, update-locked to commit after the
        -- slot lock (docs/sql-internals.md, "Execution fence").
        IF NOT EXISTS (
            SELECT 1
            FROM {{schema}}.jobs_state r WITH (UPDLOCK, ROWLOCK)
            WHERE
                r.job_id = @p_job_id
                AND r.job_status_code = 50 /* JobStatusCode.Executing */
                AND r.execution_number = @p_execution_number
                AND r.leased_by_worker_id = @p_leased_by_worker_id
        )
            THROW 50012, 'ACTA:EXECUTION_FENCED:wait_signal: the job is no longer executing this attempt.', 1;

        SELECT
            @state = checkpoint_status_code,
            @fmt = value_format_id,
            @val = value,
            @due = due_at_utc
        FROM {{schema}}.checkpoints WITH (UPDLOCK)
        WHERE job_id = @p_job_id AND checkpoint_kind_code = @p_checkpoint_kind_code AND checkpoint_name = @p_checkpoint_name;

        IF @state = 20 /* CheckpointStatusCode.Set */
            BEGIN
                SET @outcome = 2 /* WaitSignalOutcomeCode.ContinueSet */;
            END
        ELSE IF @state = 30 /* CheckpointStatusCode.Expired */
            BEGIN
                SET @outcome = 3 /* WaitSignalOutcomeCode.TimedOut */;
                SET @fmt = 0 /* JobPayloadFormat.None */;
                SET @val = NULL;
            END
        ELSE IF @state = 10 /* CheckpointStatusCode.Pending */ AND @due IS NOT NULL AND @due <= @now
            BEGIN
                UPDATE {{schema}}.checkpoints
                SET
                    checkpoint_status_code = 30 /* CheckpointStatusCode.Expired */,
                    modified_at_utc = @now,
                    version = version + 1
                WHERE job_id = @p_job_id AND checkpoint_kind_code = @p_checkpoint_kind_code AND checkpoint_name = @p_checkpoint_name;

                SET @outcome = 3 /* WaitSignalOutcomeCode.TimedOut */;
                SET @fmt = 0 /* JobPayloadFormat.None */;
                SET @val = NULL;
            END
        ELSE
            BEGIN
                SET @outcome = 1 /* WaitSignalOutcomeCode.SuspendPending */;
                IF @state IS NULL
                    BEGIN
                        INSERT INTO {{schema}}.checkpoints (
                            job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code, due_at_utc, value_format_id, value,
                            created_at_utc, modified_at_utc, version
                        )
                        VALUES (
                            @p_job_id, @p_checkpoint_kind_code, @p_checkpoint_name, @arm_state, @arm_due,
                            0 /* JobPayloadFormat.None */, NULL, @now, @now, 0
                        );
                        IF @arm_state = 30 /* CheckpointStatusCode.Expired */
                            SET @outcome = 3 /* WaitSignalOutcomeCode.TimedOut */;
                    END
                ELSE IF @state = 10 /* CheckpointStatusCode.Pending */ AND @due IS NULL AND @arm_due IS NOT NULL
                    BEGIN
                        UPDATE {{schema}}.checkpoints
                        SET
                            checkpoint_status_code = @arm_state,
                            due_at_utc = @arm_due,
                            modified_at_utc = @now,
                            version = version + 1
                        WHERE
                            job_id = @p_job_id AND checkpoint_kind_code = @p_checkpoint_kind_code AND checkpoint_name = @p_checkpoint_name
                            AND checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */
                            AND due_at_utc IS NULL;
                        IF @arm_state = 30 /* CheckpointStatusCode.Expired */
                            SET @outcome = 3 /* WaitSignalOutcomeCode.TimedOut */;
                    END
                SET @fmt = 0 /* JobPayloadFormat.None */;
                SET @val = NULL;
            END

        SELECT
            @outcome AS outcome_code,
            @fmt AS value_format_id,
            @val AS value;

        IF @entry_trancount = 0
            COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @entry_trancount = 0 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
