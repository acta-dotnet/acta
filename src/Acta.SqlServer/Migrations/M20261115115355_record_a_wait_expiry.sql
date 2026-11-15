SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO


/* Lock-first arbiter for one durable wait: Set wins even past the due, an overdue Pending flips to
   Expired, Expired replays TimedOut forever. */
/* Arming is one-directional. A NULL due_at_utc is armed when the caller carries a bound (a length, or an instant: a
   group's stored deadline), so code redeployed with a bound can un-strand a wait suspended without one; a stored due
   is never overwritten, never extended, and never cleared by a subsequent unbounded call. */
/* The read below needs no re-read after a lost insert the way pg's does: the checkpoint's own lock, taken
   first (docs/sql-internals.md, "Step, checkpoint and rate locks"), makes a concurrent first arrival wait
   here and then read the winner's row, arriving at the same resolution instead of racing the insert. */
/* A wait this call armed with a length suspends once: the length is positive and the arm is the last
   act of its branch. A checkpoint this call arms at or past an instant is armed Expired, so a child landing
   after it cannot revive the wait, and the wait times out now. Pg states the same rules. */
CREATE OR ALTER PROCEDURE {{schema}}.wait_job_signal
    @p_job_id BIGINT,
    @p_execution_number BIGINT,
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
        DECLARE @expired_due DATETIME2(3) = NULL;
        DECLARE @arm_due DATETIME2(3) = COALESCE(@p_due_at_utc, DATEADD(SECOND, @p_timeout_seconds, @now));
        DECLARE @arm_state TINYINT = CASE
            WHEN @arm_due <= @now THEN 30 /* CheckpointStatusCode.Expired */
            ELSE 10 /* CheckpointStatusCode.Pending */
        END;

        DECLARE @checkpoint_lock NVARCHAR(255) = CONCAT(N'{{schema}}.checkpoint:', @p_job_id, N':', @p_checkpoint_kind_code, N':', @p_checkpoint_name);
        DECLARE @checkpoint_lock_result INT;
        -- Positional: resource, mode, owner, timeout (-1 waits like any lock).
        EXEC @checkpoint_lock_result = sp_getapplock @checkpoint_lock, 'Exclusive', 'Transaction', -1;
        IF @checkpoint_lock_result < 0
            THROW 51205, 'ACTA:CHECKPOINT_LOCK:The checkpoint lock was not granted (deadlock victim or cancelled).', 1;

        -- The wait changes the checkpoint only while this attempt holds the job's row, update-locked to commit after the
        -- checkpoint lock (docs/sql-internals.md, "Execution fence").
        IF NOT EXISTS (
            SELECT 1
            FROM {{schema}}.jobs_state r WITH (UPDLOCK, ROWLOCK)
            WHERE
                r.job_id = @p_job_id
                AND r.job_status_code = 50 /* JobStatusCode.Executing */
                AND r.execution_number = @p_execution_number
                AND r.leased_by_worker_id = @p_leased_by_worker_id
        )
            THROW 50012, 'ACTA:EXECUTION_FENCED:wait_job_signal: the job is no longer executing this attempt.', 1;

        SELECT
            @state = checkpoint_status_code,
            @fmt = value_format_id,
            @val = value,
            @due = due_at_utc
        FROM {{schema}}.checkpoints WITH (UPDLOCK)
        WHERE job_id = @p_job_id AND checkpoint_kind_code = @p_checkpoint_kind_code AND checkpoint_name = @p_checkpoint_name;

        IF @state = 20 /* CheckpointStatusCode.Set */
            BEGIN
                SET @outcome = 2 /* WaitJobSignalOutcomeCode.ContinueSet */;
            END
        ELSE IF @state = 30 /* CheckpointStatusCode.Expired */
            BEGIN
                SET @outcome = 3 /* WaitJobSignalOutcomeCode.TimedOut */;
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

                SET @outcome = 3 /* WaitJobSignalOutcomeCode.TimedOut */;
                SET @expired_due = @due;
                SET @fmt = 0 /* JobPayloadFormat.None */;
                SET @val = NULL;
            END
        ELSE
            BEGIN
                SET @outcome = 1 /* WaitJobSignalOutcomeCode.SuspendPending */;
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
                            BEGIN
                                SET @outcome = 3 /* WaitJobSignalOutcomeCode.TimedOut */;
                                SET @expired_due = @arm_due;
                            END
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
                            BEGIN
                                SET @outcome = 3 /* WaitJobSignalOutcomeCode.TimedOut */;
                                SET @expired_due = @arm_due;
                            END
                    END
                SET @fmt = 0 /* JobPayloadFormat.None */;
                SET @val = NULL;
            END

        -- An expiry this call made reaches the timeline, a child named as its parent named it; a replayed one does not.
        IF @expired_due IS NOT NULL
            INSERT INTO {{schema}}.events (
                event_code, created_at_utc, namespace_id,
                entity_code, entity_id,
                definition_id, tenant_id, lineage_root_job_id,
                execution_number,
                event_message, details_format_id, details
            )
            SELECT
                83 /* EventCode.JobWaitExpired */, @now, j.namespace_id,
                50 /* EntityCode.Job */, j.job_id,
                j.definition_id, j.tenant_id, COALESCE(j.lineage_root_job_id, j.job_id),
                @p_execution_number,
                N'The wait for '
                + CASE
                    WHEN @p_checkpoint_kind_code = 50 /* CheckpointKindCode.ChildLatch */
                        THEN N'child ' + COALESCE(
                            N'''' + ch.deduplication_key COLLATE DATABASE_DEFAULT + N'''',
                            N'job ' + SUBSTRING(@p_checkpoint_name, 11, 128)
                        )
                    ELSE N'signal ''' + @p_checkpoint_name + N''''
                END + N' expired.',
                1 /* JobPayloadFormat.Json */,
                CAST(
                    '{"kind":"'
                    + CASE WHEN @p_checkpoint_kind_code = 50 /* CheckpointKindCode.ChildLatch */ THEN 'child-latch' ELSE 'signal' END
                    + '","name":"' + @p_checkpoint_name + '","dueAtUtc":"' + CONVERT(VARCHAR(23), @expired_due, 126) + 'Z"}'
                    AS VARBINARY(MAX)
                )
            FROM {{schema}}.jobs j
            LEFT JOIN {{schema}}.jobs ch
                ON
                    @p_checkpoint_kind_code = 50 /* CheckpointKindCode.ChildLatch */
                    AND ch.parent_job_id = j.job_id
                    AND CONCAT('sys.child.', ch.job_id) = @p_checkpoint_name
            WHERE j.job_id = @p_job_id AND j.event_recording_code = 20 /* EventRecordingCode.Audit */;

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
GO

CREATE OR ALTER VIEW {{schema}}.events_view AS
SELECT
    e.event_id,
    e.created_at_utc,
    ns.namespace_name,
    e.namespace_id,
    CASE e.entity_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'alert' WHEN 15 THEN 'calendar' WHEN 20 THEN 'checkpoint' WHEN 30 THEN 'definition' WHEN 40 THEN 'event' WHEN 50 THEN 'job' WHEN 60 THEN 'lane' WHEN 70 THEN 'lock' WHEN 80 THEN 'namespace' WHEN 90 THEN 'schedule' WHEN 100 THEN 'step' WHEN 110 THEN 'tag' WHEN 120 THEN 'tenant' WHEN 130 THEN 'worker' END AS entity,
    e.entity_code,
    e.entity_id,
    CASE e.event_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'tenant.suspended' WHEN 11 THEN 'tenant.resumed' WHEN 12 THEN 'tenant.updated' WHEN 20 THEN 'namespace.suspended' WHEN 21 THEN 'namespace.resumed' WHEN 22 THEN 'namespace.updated' WHEN 23 THEN 'namespace.deployed' WHEN 30 THEN 'definition.overrides-updated' WHEN 31 THEN 'definition.retired' WHEN 40 THEN 'job.execution-succeeded' WHEN 41 THEN 'job.execution-failed' WHEN 42 THEN 'job.execution-orphaned' WHEN 43 THEN 'job.execution-yielded' WHEN 44 THEN 'job.failed' WHEN 60 THEN 'job.suspended' WHEN 61 THEN 'job.rescheduled' WHEN 70 THEN 'job.cancelled' WHEN 71 THEN 'job.paused' WHEN 72 THEN 'job.resumed' WHEN 73 THEN 'job.restarted' WHEN 74 THEN 'job.reprioritized' WHEN 75 THEN 'job.purged' WHEN 76 THEN 'job.input-amended' WHEN 80 THEN 'job.signal-raised' WHEN 81 THEN 'job.state-reset' WHEN 82 THEN 'job.signal-cleared' WHEN 83 THEN 'job.wait-expired' WHEN 90 THEN 'job.note-recorded' WHEN 100 THEN 'schedule.paused' WHEN 101 THEN 'schedule.resumed' WHEN 102 THEN 'schedule.pause-expired' WHEN 104 THEN 'schedule.triggered' WHEN 105 THEN 'schedule.admitted' WHEN 106 THEN 'schedule.skipped' WHEN 107 THEN 'schedule.retimed' WHEN 108 THEN 'schedule.evaluation-failed' WHEN 109 THEN 'schedule.orphaned' WHEN 110 THEN 'calendar.draft-saved' WHEN 111 THEN 'calendar.published' WHEN 112 THEN 'calendar.archived' WHEN 120 THEN 'worker.started' WHEN 121 THEN 'worker.stopped' WHEN 122 THEN 'worker.died' WHEN 140 THEN 'alert.acknowledged' WHEN 141 THEN 'alert.resolved' WHEN 180 THEN 'outbox.requeued' WHEN 181 THEN 'outbox.discarded' END AS event,
    e.event_code,
    e.definition_id,
    d.definition_name,
    e.tenant_id,
    t.tenant_name,
    e.lineage_root_job_id,
    root.job_ref AS lineage_root_job_ref,
    e.worker_id,
    wkr.worker_ref,
    e.execution_number,
    e.duration_ms,
    e.event_message,
    e.actor,
    CASE e.details_format_id
        WHEN 0 /* JobPayloadFormat.None */ THEN 'none'
        WHEN 1 /* JobPayloadFormat.Json */ THEN 'json'
        WHEN 2 /* JobPayloadFormat.Bytes */ THEN 'bytes'
        ELSE CONCAT('custom-', e.details_format_id)
    END AS details_format,
    CASE
        WHEN
            e.details_format_id = 1 /* JobPayloadFormat.Json */
            THEN CAST(e.details AS VARCHAR(MAX)) COLLATE latin1_general_100_bin2_utf8
    END AS details_text
FROM {{schema}}.events AS e
JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = e.namespace_id
LEFT JOIN {{schema}}.definitions AS d ON d.definition_id = e.definition_id
LEFT JOIN {{schema}}.tenants AS t
    ON t.tenant_id = COALESCE(e.tenant_id, CASE WHEN e.entity_code = 120 /* EntityCode.Tenant */ THEN e.entity_id END)
LEFT JOIN {{schema}}.jobs AS root ON root.job_id = e.lineage_root_job_id
LEFT JOIN {{schema}}.workers AS wkr ON wkr.worker_id = e.worker_id
GO


IF NOT EXISTS (SELECT 1 FROM {{schema}}.migrations WHERE name = 'M20261115115355_record_a_wait_expiry')
INSERT INTO {{schema}}.migrations (name)
VALUES ('M20261115115355_record_a_wait_expiry');
GO
