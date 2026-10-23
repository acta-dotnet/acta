CREATE OR ALTER PROCEDURE {{schema}}.raise_signal
    @p_job_id BIGINT,
    @p_checkpoint_kind_code TINYINT,
    @p_checkpoint_name VARCHAR(128),
    @p_value_format_id TINYINT,
    @p_value VARBINARY(MAX),
    @p_event_message NVARCHAR(512),
    @p_details_format_id TINYINT,
    @p_details VARBINARY(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
        DECLARE @existing TINYINT;
        DECLARE @expired BIT = 0;
        DECLARE @message NVARCHAR(512);
        DECLARE
            @from_status TINYINT, @namespace_id INT, @lineage_root_job_id BIGINT,
            @definition_id INT, @tenant_id INT, @execution_number INT, @event_recording TINYINT;

        -- The slot's own lock, as wait_signal names it (docs/sql-internals.md, "Slot locks").
        DECLARE @slot_lock NVARCHAR(255) = CONCAT(N'{{schema}}.slot:', @p_job_id, N':', @p_checkpoint_kind_code, N':', @p_checkpoint_name);
        DECLARE @slot_lock_result INT;
        -- Positional: resource, mode, owner, timeout (-1 waits like any lock).
        EXEC @slot_lock_result = sp_getapplock @slot_lock, 'Exclusive', 'Transaction', -1;
        IF @slot_lock_result < 0
            THROW 51205, 'ACTA:SLOT_LOCK:The checkpoint slot lock was not granted (deadlock victim or cancelled).', 1;

        SELECT @existing = checkpoint_status_code
        FROM {{schema}}.checkpoints WITH (UPDLOCK)
        WHERE job_id = @p_job_id AND checkpoint_kind_code = @p_checkpoint_kind_code AND checkpoint_name = @p_checkpoint_name;

        SELECT
            @from_status = r.job_status_code,
            @namespace_id = j.namespace_id,
            @lineage_root_job_id = j.lineage_root_job_id,
            @definition_id = j.definition_id,
            @tenant_id = j.tenant_id,
            @execution_number = r.execution_number,
            @event_recording = j.event_recording_code
        FROM {{schema}}.jobs_state r WITH (UPDLOCK, ROWLOCK)
        INNER JOIN {{schema}}.jobs j ON j.job_id = r.job_id
        WHERE r.job_id = @p_job_id;

        IF @from_status IS NULL
            BEGIN

                SELECT
                    CAST(2 /* ControlAction.NotFound */ AS TINYINT) AS action,
                    CAST(NULL AS TINYINT) AS status;
                GOTO Finish;
            END;

        IF @from_status IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
            BEGIN

                SELECT
                    CAST(3 /* ControlAction.Rejected */ AS TINYINT) AS action,
                    @from_status AS status;
                GOTO Finish;
            END;

        /* No revival: an Expired slot already resolved the wait TimedOut, so a late raise writes no
           slot and releases no job. The raise still happened, so it is still recorded: the event below
           carries a message saying why it changed nothing, and the verb reports the unchanged status. */
        SET @expired = CASE WHEN @existing = 30 /* CheckpointStatusCode.Expired */ THEN 1 ELSE 0 END;
        SET @message = CASE
            WHEN @expired = 1
                THEN LEFT(COALESCE(@p_event_message + N' ', N'') + N'Signal not applied: the wait had already expired.', 512)
            ELSE @p_event_message
        END;

        IF @expired = 0
            BEGIN
                IF @existing IS NULL
                    INSERT INTO {{schema}}.checkpoints (
                        job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code, value_format_id, value,
                        created_at_utc, modified_at_utc, version
                    )
                    VALUES (
                        @p_job_id, @p_checkpoint_kind_code, @p_checkpoint_name, 20 /* CheckpointStatusCode.Set */,
                        @p_value_format_id, @p_value, @now, @now, 0
                    );
                ELSE
                    UPDATE {{schema}}.checkpoints
                    SET
                        checkpoint_status_code = 20 /* CheckpointStatusCode.Set */,
                        value_format_id = @p_value_format_id,
                        value = @p_value,
                        modified_at_utc = @now,
                        version = version + 1
                    WHERE
                        job_id = @p_job_id AND checkpoint_kind_code = @p_checkpoint_kind_code AND checkpoint_name = @p_checkpoint_name;
            END

        IF @event_recording = 20 /* EventRecordingCode.Audit */
            INSERT INTO {{schema}}.events (
                event_code, created_at_utc, namespace_id,
                entity_code, entity_id,
                definition_id, tenant_id, lineage_root_job_id,
                execution_number,
                event_message, details_format_id, details
            )
            VALUES (
                80 /* EventCode.JobSignalRaised */, @now, @namespace_id,
                50 /* EntityCode.Job */, @p_job_id,
                @definition_id, @tenant_id, COALESCE(@lineage_root_job_id, @p_job_id),
                @execution_number,
                @message, @p_details_format_id, @p_details
            );

        IF @from_status = 20 /* JobStatusCode.Suspended */ AND @expired = 0
            BEGIN
                UPDATE {{schema}}.jobs_state
                SET
                    job_status_code = 10 /* JobStatusCode.Ready */,
                    next_run_at_utc = @now,
                    modified_at_utc = @now,
                    version = version + 1
                WHERE job_id = @p_job_id;

                IF @event_recording = 20 /* EventRecordingCode.Audit */
                    INSERT INTO {{schema}}.events (
                        event_code, created_at_utc, namespace_id,
                        entity_code, entity_id,
                        definition_id, tenant_id, lineage_root_job_id,
                        execution_number,
                        event_message, details_format_id, details
                    )
                    VALUES (
                        72 /* EventCode.JobResumed */, @now, @namespace_id,
                        50 /* EntityCode.Job */, @p_job_id,
                        @definition_id, @tenant_id, COALESCE(@lineage_root_job_id, @p_job_id),
                        @execution_number,
                        @p_event_message, @p_details_format_id, @p_details
                    );

                SELECT
                    CAST(1 /* ControlAction.Applied */ AS TINYINT) AS action,
                    CAST(10 /* JobStatusCode.Ready */ AS TINYINT) AS status;
                GOTO Finish;
            END

        SELECT
            CAST(1 /* ControlAction.Applied */ AS TINYINT) AS action,
            @from_status AS status;

    Finish:

        IF @entry_trancount = 0
            COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @entry_trancount = 0 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
