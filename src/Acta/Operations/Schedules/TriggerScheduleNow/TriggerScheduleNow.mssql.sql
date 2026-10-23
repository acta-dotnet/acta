SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @found BIT = 0, @status TINYINT, @paused DATETIME2(7), @next DATETIME2(7), @version INT;
    DECLARE @slot_status TINYINT, @slot_next DATETIME2(7);
    DECLARE @ns INT, @def INT, @lineage BIGINT, @en INT, @event_recording TINYINT;
    DECLARE @lock_result INT, @lock_resource NVARCHAR(255);

    /* The namespace's schedule lock, shared, before any row lock (docs/sql-internals.md, "Schedule lock"), so
       the reads below see a retire or registration that committed. */
    SELECT @lock_resource = N'{{schema}}.schedules.' + CAST(j.namespace_id AS NVARCHAR(12))
    FROM {{schema}}.jobs j
    WHERE j.job_id = @p_job_id;

    IF @lock_resource IS NOT NULL
        BEGIN
            EXEC @lock_result = sys.sp_getapplock @lock_resource, 'Shared', 'Transaction', -1;
            IF @lock_result < 0
                THROW 51205, 'ACTA:SCHEDULE_LOCK:The namespace schedule lock was not granted (deadlock victim or cancelled).', 1;
        END

    /* The slot's jobs_state row locks before its schedules row, the order a fire's completion takes them in. The
       schedules row is guard-only here (never updated): a manual trigger moves only the slot's cursor. */
    SELECT
        @slot_status = r.job_status_code,
        @slot_next = r.next_run_at_utc,
        @en = r.execution_number
    FROM {{schema}}.jobs_state r WITH (UPDLOCK, ROWLOCK)
    WHERE r.job_id = @p_job_id;

    SELECT
        @found = 1,
        @status = js.schedule_status_code,
        @paused = js.paused_until_utc,
        @next = st.next_occurrence_at_utc,
        @version = js.version
    FROM {{schema}}.schedules js WITH (UPDLOCK, ROWLOCK)
    INNER JOIN {{schema}}.definitions d ON d.definition_id = js.definition_id
    INNER JOIN {{schema}}.schedules_state st ON st.job_id = js.job_id AND st.schedule_name = js.schedule_name
    WHERE
        js.job_id = @p_job_id
        AND js.schedule_name = @p_schedule_name
        AND js.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
        AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */;

    IF @found = 0
        BEGIN

            SELECT
                CAST(2 /* ControlAction.NotFound */ AS TINYINT) AS action,
                CAST(NULL AS TINYINT) AS status,
                CAST(NULL AS DATETIME2(7)) AS paused_until_utc,
                CAST(NULL AS DATETIME2(7)) AS next_occurrence_at_utc,
                CAST(NULL AS INT) AS version;
            GOTO Finish;
        END;

    IF @status = 30 /* ScheduleStatusCode.Paused */
        BEGIN

            SELECT
                CAST(3 /* ControlAction.Rejected */ AS TINYINT) AS action,
                @status AS status,
                @paused AS paused_until_utc,
                @next AS next_occurrence_at_utc,
                @version AS version;
            GOTO Finish;
        END;

    -- A held job (Paused with a next run; ScheduleWalker, "held") runs only once it is resumed.
    IF @slot_status = 50 /* JobStatusCode.Executing */
        OR (@slot_status = 30 /* JobStatusCode.Paused */ AND @slot_next IS NOT NULL)
        BEGIN

            SELECT
                CAST(3 /* ControlAction.Rejected */ AS TINYINT) AS action,
                @status AS status,
                @paused AS paused_until_utc,
                @next AS next_occurrence_at_utc,
                @version AS version;
            GOTO Finish;
        END;

    IF @slot_status IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
        BEGIN

            SELECT
                CAST(3 /* ControlAction.Rejected */ AS TINYINT) AS action,
                @status AS status,
                @paused AS paused_until_utc,
                @next AS next_occurrence_at_utc,
                @version AS version;
            GOTO Finish;
        END;

    SELECT
        @ns = j.namespace_id,
        @def = j.definition_id,
        @lineage = j.lineage_root_job_id,
        @event_recording = j.event_recording_code
    FROM {{schema}}.jobs j
    WHERE j.job_id = @p_job_id;

    UPDATE {{schema}}.jobs_state
    SET
        next_run_at_utc = @now,
        job_status_code = 10 /* JobStatusCode.Ready */,
        modified_at_utc = @now,
        version = version + 1
    WHERE
        job_id = @p_job_id
        AND (
            job_status_code IN (20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */)
            OR (job_status_code = 30 /* JobStatusCode.Paused */ AND next_run_at_utc IS NULL)
        );

    IF @event_recording = 20 /* EventRecordingCode.Audit */
        BEGIN
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
        CAST(1 /* ControlAction.Applied */ AS TINYINT) AS action,
        @status AS status,
        @paused AS paused_until_utc,
        @next AS next_occurrence_at_utc,
        @version AS version;

Finish:

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
