SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @found BIT = 0, @status TINYINT, @paused DATETIME2(7), @next DATETIME2(7), @version INT;
    DECLARE @ns INT, @def INT, @slot BIGINT, @lineage BIGINT, @en INT, @event_recording TINYINT;
    DECLARE @lock_result INT, @lock_resource NVARCHAR(255);

    /* The namespace's schedule lock, shared, before any row lock (docs/sql-internals.md, "Schedule lock"), so
       the reads below see a retire or registration that committed. */
    SELECT
        @lock_resource = N'{{schema}}.schedules.' + CAST(js.namespace_id AS NVARCHAR(12)),
        @slot = js.job_id
    FROM {{schema}}.schedules js
    WHERE js.schedule_id = @p_schedule_id;

    IF @lock_resource IS NOT NULL
        BEGIN
            EXEC @lock_result = sys.sp_getapplock @lock_resource, 'Shared', 'Transaction', -1;
            IF @lock_result < 0
                THROW 51205, 'ACTA:SCHEDULE_LOCK:The namespace schedule lock was not granted (deadlock victim or cancelled).', 1;
        END

    -- A slot's jobs_state row locks before its schedules row, the order a fire's completion takes them in.
    SELECT @en = r.execution_number
    FROM {{schema}}.jobs_state r WITH (UPDLOCK, ROWLOCK)
    WHERE r.job_id = @slot;

    SELECT @found = 1
    FROM {{schema}}.schedules js WITH (UPDLOCK, ROWLOCK)
    INNER JOIN {{schema}}.definitions d ON d.definition_id = js.definition_id
    WHERE
        js.schedule_id = @p_schedule_id
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

    SELECT
        @ns = js.namespace_id,
        @def = js.definition_id,
        @lineage = j.lineage_root_job_id,
        @event_recording = COALESCE(j.event_recording_code, d.event_recording_code_effective)
    FROM {{schema}}.schedules js
    INNER JOIN {{schema}}.definitions d ON d.definition_id = js.definition_id
    LEFT JOIN {{schema}}.jobs j ON j.job_id = js.job_id
    WHERE js.schedule_id = @p_schedule_id;

    UPDATE {{schema}}.schedules
    SET
        schedule_status_code = 10 /* ScheduleStatusCode.Active */,
        paused_until_utc = NULL,
        pause_message = NULL,
        modified_at_utc = @now,
        version = version + 1
    WHERE schedule_id = @p_schedule_id;

    UPDATE {{schema}}.schedules_state
    SET
        -- The remembered cursor stays; the admitter applies the misfire policy to it at its next pass.
        next_evaluation_at_utc = @now,
        evaluation_status_code = 60 /* EvaluationStatusCode.Resumed */,
        version = version + 1,
        modified_at_utc = @now
    WHERE schedule_id = @p_schedule_id;

    SELECT
        @status = js.schedule_status_code,
        @paused = js.paused_until_utc,
        @next = st.next_occurrence_at_utc,
        @version = js.version
    FROM {{schema}}.schedules js
    INNER JOIN {{schema}}.schedules_state st ON st.schedule_id = js.schedule_id
    WHERE js.schedule_id = @p_schedule_id;


    -- Audited against the slot, or against the definition when the schedule has none.
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
                101 /* EventCode.ScheduleResumed */, @now, @ns,
                CASE WHEN @slot IS NULL THEN 30 /* EntityCode.Definition */ ELSE 50 /* EntityCode.Job */ END, COALESCE(@slot, @def),
                @def, COALESCE(@lineage, @slot),
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
