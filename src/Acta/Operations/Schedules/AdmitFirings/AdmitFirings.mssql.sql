SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @lock_result INT;
    DECLARE @lock_resource NVARCHAR(255) = N'{{schema}}.schedules.' + CAST(@p_namespace_id AS NVARCHAR(12));

    DECLARE @groups TABLE (
        job_id BIGINT NOT NULL PRIMARY KEY,
        expected_job_version INT NOT NULL,
        admit BIT NOT NULL,
        envelope NVARCHAR(MAX) NULL);
    DECLARE @rows TABLE (
        schedule_id INT NOT NULL PRIMARY KEY,
        job_id BIGINT NOT NULL,
        schedule_name VARCHAR(128) NOT NULL,
        expected_version INT NOT NULL,
        next_occurrence_at_utc DATETIME2(3) NULL,
        decided_through_at_utc DATETIME2(3) NULL,
        next_evaluation_at_utc DATETIME2(3) NULL,
        evaluation_status_code TINYINT NOT NULL,
        pause_ended BIT NOT NULL,
        admitted_details NVARCHAR(MAX) NULL,
        skipped_details NVARCHAR(MAX) NULL);
    DECLARE @slots TABLE (job_id BIGINT NOT NULL PRIMARY KEY, valid BIT NOT NULL);
    DECLARE @valid TABLE (job_id BIGINT NOT NULL PRIMARY KEY);

    INSERT INTO @groups (job_id, expected_job_version, admit, envelope)
    SELECT job_id, expected_job_version, admit, envelope
    FROM OPENJSON(@p_groups) WITH (
        job_id BIGINT '$.job_id', expected_job_version INT '$.expected_job_version', admit BIT '$.admit', envelope NVARCHAR(MAX) '$.envelope');

    INSERT INTO @rows (
        schedule_id, job_id, schedule_name, expected_version,
        next_occurrence_at_utc, decided_through_at_utc, next_evaluation_at_utc, evaluation_status_code,
        pause_ended, admitted_details, skipped_details)
    SELECT
        schedule_id, job_id, schedule_name, expected_version,
        next_occurrence_at_utc, decided_through_at_utc, next_evaluation_at_utc, evaluation_status_code,
        pause_ended, admitted_details, skipped_details
    FROM OPENJSON(@p_rows) WITH (
        schedule_id INT '$.schedule_id', job_id BIGINT '$.job_id', schedule_name VARCHAR(128) '$.schedule_name',
        expected_version INT '$.expected_version',
        next_occurrence_at_utc DATETIME2(3) '$.next_occurrence_at_utc', decided_through_at_utc DATETIME2(3) '$.decided_through_at_utc',
        next_evaluation_at_utc DATETIME2(3) '$.next_evaluation_at_utc', evaluation_status_code TINYINT '$.evaluation_status_code',
        pause_ended BIT '$.pause_ended', admitted_details NVARCHAR(MAX) '$.admitted_details', skipped_details NVARCHAR(MAX) '$.skipped_details');

    -- The namespace's schedule lock, shared, before any row lock (docs/sql-internals.md, "Schedule lock").
    EXEC @lock_result = sys.sp_getapplock @lock_resource, 'Shared', 'Transaction', -1;
    IF @lock_result < 0
        THROW 51205, 'ACTA:SCHEDULE_LOCK:The namespace schedule lock was not granted (deadlock victim or cancelled).', 1;

    -- Slot rows in id order, then the members' state rows in id order, each a key seek under an update lock. An
    -- admitting group needs the slot Idle at the version it read; a cursor-only group needs only the version.
    INSERT INTO @slots (job_id, valid)
    SELECT
        g.job_id,
        CASE WHEN r.version = g.expected_job_version AND (g.admit = 0 OR r.job_status_code = 40 /* JobStatusCode.Idle */) THEN 1 ELSE 0 END
    FROM @groups g
    INNER JOIN {{schema}}.jobs_state r WITH (UPDLOCK, ROWLOCK, FORCESEEK) ON r.job_id = g.job_id;

    INSERT INTO @valid (job_id)
    SELECT s.job_id
    FROM @slots s
    WHERE
        s.valid = 1
        AND NOT EXISTS (
            SELECT 1
            FROM @rows a
            INNER JOIN {{schema}}.schedules_state st WITH (UPDLOCK, ROWLOCK, FORCESEEK) ON st.schedule_id = a.schedule_id
            WHERE a.job_id = s.job_id AND st.version <> a.expected_version
        );

    UPDATE r
    SET
        job_status_code = 10 /* JobStatusCode.Ready */,
        next_run_at_utc = @now,
        failure_count = 0,
        modified_at_utc = @now,
        version = r.version + 1
    FROM {{schema}}.jobs_state r WITH (FORCESEEK)
    INNER JOIN @groups g ON g.job_id = r.job_id
    INNER JOIN @valid v ON v.job_id = g.job_id
    WHERE g.admit = 1;

    -- The envelope replaces the settled one of the previous firing; a Pending one never exists on an Idle slot. The
    -- JSON is ASCII by construction, so the VARCHAR cast is its UTF-8.
    UPDATE cp
    SET
        checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */,
        value_format_id = 1 /* JobPayloadFormat.Json */,
        value = CAST(CAST(g.envelope AS VARCHAR(MAX)) AS VARBINARY(MAX)),
        modified_at_utc = @now,
        version = cp.version + 1
    FROM {{schema}}.checkpoints cp WITH (FORCESEEK)
    INNER JOIN @groups g ON g.job_id = cp.job_id
    INNER JOIN @valid v ON v.job_id = g.job_id
    WHERE
        g.admit = 1
        AND cp.checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */
        AND cp.checkpoint_name = 'sys.firing';

    INSERT INTO {{schema}}.checkpoints (
        job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code,
        value_format_id, value, created_at_utc, modified_at_utc, version)
    SELECT
        g.job_id, 60 /* CheckpointKindCode.Firing */, 'sys.firing', 10 /* CheckpointStatusCode.Pending */,
        1 /* JobPayloadFormat.Json */, CAST(CAST(g.envelope AS VARCHAR(MAX)) AS VARBINARY(MAX)), @now, @now, 0
    FROM @groups g
    INNER JOIN @valid v ON v.job_id = g.job_id
    WHERE
        g.admit = 1
        AND NOT EXISTS (
            SELECT 1
            FROM {{schema}}.checkpoints cp
            WHERE
                cp.job_id = g.job_id
                AND cp.checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */
                AND cp.checkpoint_name = 'sys.firing'
        );

    UPDATE st
    SET
        next_occurrence_at_utc = a.next_occurrence_at_utc,
        decided_through_at_utc = a.decided_through_at_utc,
        next_evaluation_at_utc = a.next_evaluation_at_utc,
        evaluation_status_code = a.evaluation_status_code,
        modified_at_utc = @now,
        version = st.version + 1
    FROM {{schema}}.schedules_state st WITH (FORCESEEK)
    INNER JOIN @rows a ON a.schedule_id = st.schedule_id
    INNER JOIN @valid v ON v.job_id = a.job_id;

    -- An elapsed timed pause ends here, the one place the admitter touches the schedules row.
    UPDATE js
    SET
        schedule_status_code = 10 /* ScheduleStatusCode.Active */,
        paused_until_utc = NULL,
        pause_message = NULL,
        modified_at_utc = @now,
        version = js.version + 1
    FROM {{schema}}.schedules js WITH (FORCESEEK)
    INNER JOIN @rows a ON a.schedule_id = js.schedule_id
    INNER JOIN @valid v ON v.job_id = a.job_id
    WHERE a.pause_ended = 1 AND js.schedule_status_code = 30 /* ScheduleStatusCode.Paused */;

    -- Decision evidence at Audit: an admission, a skipped range, an elapsed pause. A schedule name is dotted kebab
    -- ASCII, and the details are ASCII JSON, so the VARCHAR cast is their UTF-8.
    INSERT INTO {{schema}}.events (
        event_code, created_at_utc, namespace_id,
        entity_code, entity_id,
        definition_id, tenant_id, lineage_root_job_id,
        execution_number,
        details_format_id, details
    )
    SELECT
        k.event_code, @now, j.namespace_id,
        50 /* EntityCode.Job */, j.job_id,
        j.definition_id, j.tenant_id, COALESCE(j.lineage_root_job_id, j.job_id),
        r.execution_number,
        1 /* JobPayloadFormat.Json */, CAST(CAST(k.details AS VARCHAR(MAX)) AS VARBINARY(MAX))
    FROM (
        SELECT a.schedule_id, a.job_id, 1 AS ord, 105 /* EventCode.ScheduleAdmitted */ AS event_code, a.admitted_details AS details
        FROM @rows a
        UNION ALL
        SELECT a.schedule_id, a.job_id, 2, 106 /* EventCode.ScheduleSkipped */, a.skipped_details
        FROM @rows a
        UNION ALL
        SELECT a.schedule_id, a.job_id, 3, 102 /* EventCode.SchedulePauseExpired */, N'{"schedule":"' + a.schedule_name + N'"}'
        FROM @rows a
        WHERE a.pause_ended = 1
    ) k
    INNER JOIN @valid v ON v.job_id = k.job_id
    INNER JOIN {{schema}}.jobs j ON j.job_id = k.job_id
    INNER JOIN {{schema}}.jobs_state r ON r.job_id = k.job_id
    WHERE j.event_recording_code = 20 /* EventRecordingCode.Audit */ AND k.details IS NOT NULL
    ORDER BY k.schedule_id, k.ord;

    SELECT s.job_id, CAST(CASE WHEN v.job_id IS NULL THEN 0 ELSE 1 END AS BIT) AS applied
    FROM @slots s
    LEFT JOIN @valid v ON v.job_id = s.job_id;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
