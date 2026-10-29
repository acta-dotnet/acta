SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @found BIT = 0, @status TINYINT, @paused DATETIME2(7), @next DATETIME2(7), @version INT;
    DECLARE @slot_status TINYINT, @firing_status TINYINT, @action TINYINT;
    DECLARE @ns INT, @def INT, @lineage BIGINT, @en INT, @event_recording TINYINT;
    DECLARE @lock_result INT, @lock_resource NVARCHAR(255);

    /* The namespace's schedule lock, shared, before any row lock (docs/sql-internals.md, "Schedule lock"), then the
       slot's jobs_state row before its schedules row, the order an admission takes them in: a manual firing is an
       admission with no contributors. */
    SELECT
        @lock_resource = N'{{schema}}.schedules.' + CAST(j.namespace_id AS NVARCHAR(12)),
        @ns = j.namespace_id,
        @def = j.definition_id,
        @lineage = j.lineage_root_job_id,
        @event_recording = j.event_recording_code
    FROM {{schema}}.jobs j
    WHERE j.job_id = @p_job_id;

    IF @lock_resource IS NOT NULL
        BEGIN
            EXEC @lock_result = sys.sp_getapplock @lock_resource, 'Shared', 'Transaction', -1;
            IF @lock_result < 0
                THROW 51205, 'ACTA:SCHEDULE_LOCK:The namespace schedule lock was not granted (deadlock victim or cancelled).', 1;
        END

    SELECT
        @slot_status = r.job_status_code,
        @en = r.execution_number,
        @firing_status = cp.checkpoint_status_code
    FROM {{schema}}.jobs_state r WITH (UPDLOCK, ROWLOCK)
    LEFT JOIN {{schema}}.checkpoints cp
        ON cp.job_id = r.job_id AND cp.checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */ AND cp.checkpoint_name = 'sys.firing'
    WHERE r.job_id = @p_job_id;

    SELECT
        @found = 1,
        @status = js.schedule_status_code,
        @paused = js.paused_until_utc,
        @next = st.next_occurrence_at_utc,
        @version = js.version
    FROM {{schema}}.schedules js WITH (UPDLOCK, ROWLOCK)
    INNER JOIN {{schema}}.definitions d ON d.definition_id = js.definition_id
    INNER JOIN {{schema}}.schedules_state st ON st.schedule_id = js.schedule_id
    WHERE
        js.schedule_id = @p_schedule_id
        AND js.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
        AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */;

    -- Only an Idle slot takes a manual firing: a busy one is Rejected rather than queued behind, and a held, cancelled,
    -- or finished one needs its operator verb first. A paused schedule is Rejected too.
    SET @action = CASE
        WHEN @found = 0 THEN 2 /* ControlAction.NotFound */
        WHEN @status = 30 /* ScheduleStatusCode.Paused */ THEN 3 /* ControlAction.Rejected */
        WHEN @slot_status = 40 /* JobStatusCode.Idle */ AND COALESCE(@firing_status, 0) <> 10 /* CheckpointStatusCode.Pending */
            THEN 1 /* ControlAction.Applied */
        ELSE 3 /* ControlAction.Rejected */
    END;

    IF @action = 1 /* ControlAction.Applied */
        BEGIN
            UPDATE {{schema}}.jobs_state
            SET
                next_run_at_utc = @now,
                job_status_code = 10 /* JobStatusCode.Ready */,
                failure_count = 0,
                modified_at_utc = @now,
                version = version + 1
            WHERE job_id = @p_job_id;

            -- The JSON is ASCII by construction, so the VARCHAR cast is its UTF-8.
            UPDATE {{schema}}.checkpoints
            SET
                checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */,
                value_format_id = 1 /* JobPayloadFormat.Json */,
                value = CAST(CAST(@p_envelope AS VARCHAR(MAX)) AS VARBINARY(MAX)),
                modified_at_utc = @now,
                version = version + 1
            WHERE
                job_id = @p_job_id
                AND checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */
                AND checkpoint_name = 'sys.firing';

            IF @@ROWCOUNT = 0
                INSERT INTO {{schema}}.checkpoints (
                    job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code,
                    value_format_id, value, created_at_utc, modified_at_utc, version)
                VALUES (
                    @p_job_id, 60 /* CheckpointKindCode.Firing */, 'sys.firing', 10 /* CheckpointStatusCode.Pending */,
                    1 /* JobPayloadFormat.Json */, CAST(CAST(@p_envelope AS VARCHAR(MAX)) AS VARBINARY(MAX)), @now, @now, 0);

            IF @event_recording = 20 /* EventRecordingCode.Audit */
                INSERT INTO {{schema}}.events (
                    event_code, created_at_utc, namespace_id,
                    entity_code, entity_id,
                    definition_id, lineage_root_job_id,
                    execution_number,
                    event_message, details_format_id, details
                )
                VALUES (
                    104 /* EventCode.ScheduleTriggered */, @now, @ns,
                    50 /* EntityCode.Job */, @p_job_id,
                    @def, COALESCE(@lineage, @p_job_id),
                    @en,
                    @p_event_message, @p_details_format_id, @p_details
                );
        END

    SELECT
        @action AS action,
        @status AS status,
        @paused AS paused_until_utc,
        @next AS next_occurrence_at_utc,
        @version AS version;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
