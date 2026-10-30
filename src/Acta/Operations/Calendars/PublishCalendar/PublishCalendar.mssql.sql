SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @lock_result INT;
    DECLARE @lock_resource NVARCHAR(255);

    DECLARE @rows TABLE (
        schedule_id INT NOT NULL PRIMARY KEY,
        expected_version INT NOT NULL,
        expected_state_version INT NOT NULL,
        next_occurrence_at_utc DATETIME2(7) NULL,
        retimed_details NVARCHAR(MAX) NULL,
        skipped_details NVARCHAR(MAX) NULL
    );
    INSERT INTO @rows
    SELECT
        r.schedule_id, r.expected_version, r.expected_state_version, r.next_occurrence_at_utc, r.retimed_details, r.skipped_details
    FROM OPENJSON (@p_rows)
    WITH (
        schedule_id INT '$.schedule_id', expected_version INT '$.expected_version',
        expected_state_version INT '$.expected_state_version', next_occurrence_at_utc DATETIME2(7) '$.next_occurrence_at_utc',
        retimed_details NVARCHAR(MAX) '$.retimed_details', skipped_details NVARCHAR(MAX) '$.skipped_details') r;

    /* The schedule lock of every namespace the plan touches, exclusive, in namespace id order, before any row lock: a
       publication retimes schedules the way a deploy does, and a shared calendar's schedules span namespaces. */
    DECLARE @locks TABLE (namespace_id INT NOT NULL PRIMARY KEY);
    INSERT INTO @locks (namespace_id)
    SELECT @p_namespace_id
    UNION
    SELECT s.namespace_id FROM {{schema}}.schedules s WHERE s.schedule_id IN (SELECT r.schedule_id FROM @rows r);
    DECLARE @ns INT = (SELECT MIN(l.namespace_id) FROM @locks l);
    WHILE @ns IS NOT NULL
    BEGIN
        SET @lock_resource = N'{{schema}}.schedules.' + CAST(@ns AS NVARCHAR(12));
        EXEC @lock_result = sys.sp_getapplock @lock_resource, 'Exclusive', 'Transaction', -1;
        IF @lock_result < 0
            THROW 51205, 'ACTA:SCHEDULE_LOCK:The namespace schedule lock was not granted (deadlock victim or cancelled).', 1;
        SET @ns = (SELECT MIN(l.namespace_id) FROM @locks l WHERE l.namespace_id > @ns);
    END

    -- The candidate must still be the draft the plan saw, and the family's current version the one the caller expects.
    DECLARE @candidate_id INT = (
        SELECT c.calendar_id FROM {{schema}}.calendars c WITH (UPDLOCK, HOLDLOCK)
        WHERE c.namespace_id = @p_namespace_id AND c.calendar_name = @p_calendar_name AND c.version = @p_candidate_version
          AND c.calendar_status_code = 10 /* CalendarStatusCode.Draft */);
    DECLARE @current_id INT = (
        SELECT c.calendar_id FROM {{schema}}.calendars c WITH (UPDLOCK, HOLDLOCK)
        WHERE c.namespace_id = @p_namespace_id AND c.calendar_name = @p_calendar_name
          AND c.calendar_status_code = 20 /* CalendarStatusCode.Published */);
    DECLARE @current_version INT = (SELECT c.version FROM {{schema}}.calendars c WHERE c.calendar_id = @current_id);

    DECLARE @valid BIT = 0;
    IF @candidate_id IS NOT NULL AND COALESCE(@current_version, 0) = @p_expected_current_version
        AND NOT EXISTS (
            SELECT 1
            FROM @rows r
            LEFT JOIN {{schema}}.schedules s ON s.schedule_id = r.schedule_id
            LEFT JOIN {{schema}}.schedules_state st WITH (UPDLOCK, HOLDLOCK) ON st.schedule_id = r.schedule_id
            WHERE s.version IS NULL OR s.version <> r.expected_version OR st.version IS NULL OR st.version <> r.expected_state_version)
        SET @valid = 1;

    IF @valid = 1
    BEGIN
        UPDATE {{schema}}.calendars
        SET calendar_status_code = 30 /* CalendarStatusCode.Superseded */, modified_at_utc = @now
        WHERE calendar_id = @current_id;

        UPDATE {{schema}}.calendars
        SET calendar_status_code = 20 /* CalendarStatusCode.Published */, published_at_utc = @now, modified_at_utc = @now
        WHERE calendar_id = @candidate_id;

        -- The retime events are written before the cursor moves, as registration does, from the rows the plan carries.
        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id,
            entity_code, entity_id,
            definition_id, tenant_id, lineage_root_job_id,
            execution_number,
            details_format_id, details
        )
        SELECT
            e.event_code, @now, s.namespace_id,
            CASE WHEN s.job_id IS NULL THEN 30 /* EntityCode.Definition */ ELSE 50 /* EntityCode.Job */ END,
            COALESCE(s.job_id, s.definition_id),
            s.definition_id, j.tenant_id, COALESCE(j.lineage_root_job_id, j.job_id),
            jr.execution_number,
            1 /* JobPayloadFormat.Json */, CAST(CAST(e.details AS VARCHAR(MAX)) AS VARBINARY(MAX))
        FROM @rows r
        INNER JOIN {{schema}}.schedules s ON s.schedule_id = r.schedule_id
        INNER JOIN {{schema}}.definitions d ON d.definition_id = s.definition_id
        LEFT JOIN {{schema}}.jobs j ON j.job_id = s.job_id
        LEFT JOIN {{schema}}.jobs_state jr ON jr.job_id = s.job_id
        CROSS APPLY (
            SELECT 106 /* EventCode.ScheduleSkipped */ AS event_code, r.skipped_details AS details, 1 AS ord
            UNION ALL
            SELECT 107 /* EventCode.ScheduleRetimed */, r.retimed_details, 2
        ) e
        WHERE
            e.details IS NOT NULL
            AND COALESCE(j.event_recording_code, d.event_recording_code_effective) = 20 /* EventRecordingCode.Audit */
        ORDER BY r.schedule_id, e.ord;

        UPDATE s
        SET
            modified_at_utc = @now,
            version = s.version + 1
        FROM {{schema}}.schedules s
        INNER JOIN @rows r ON r.schedule_id = s.schedule_id;

        -- The control state stays: a paused schedule wakes at its pause's end, an orphaned one not at all.
        UPDATE st
        SET
            next_occurrence_at_utc = r.next_occurrence_at_utc,
            next_evaluation_at_utc = CASE
                WHEN s.schedule_status_code = 30 /* ScheduleStatusCode.Paused */ THEN s.paused_until_utc
                WHEN s.schedule_status_code = 230 /* ScheduleStatusCode.Orphaned */ THEN NULL
                ELSE r.next_occurrence_at_utc END,
            evaluation_status_code = CASE WHEN r.next_occurrence_at_utc IS NULL THEN 30 /* EvaluationStatusCode.Exhausted */ ELSE 10 /* EvaluationStatusCode.Scheduled */ END,
            version = st.version + 1,
            modified_at_utc = @now
        FROM {{schema}}.schedules_state st
        INNER JOIN @rows r ON r.schedule_id = st.schedule_id
        INNER JOIN {{schema}}.schedules s ON s.schedule_id = r.schedule_id;

        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id,
            entity_code, entity_id,
            event_message, details_format_id, details
        )
        VALUES (
            111 /* EventCode.CalendarPublished */, @now, @p_namespace_id,
            15 /* EntityCode.Calendar */, @candidate_id,
            @p_event_message, @p_details_format_id, @p_details
        );
    END

    SELECT
        CAST(CASE WHEN @valid = 1 THEN 1 /* ControlAction.Applied */ WHEN @candidate_id IS NULL THEN 2 /* ControlAction.NotFound */ ELSE 3 /* ControlAction.Rejected */ END AS SMALLINT) AS action,
        (SELECT COUNT(*) FROM @rows) AS affected,
        @current_version AS current_version;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
