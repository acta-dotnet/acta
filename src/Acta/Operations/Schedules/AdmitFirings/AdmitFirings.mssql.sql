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
        ordinal INT NOT NULL PRIMARY KEY,
        job_id BIGINT NULL,
        expected_job_version INT NULL,
        schedule_id INT NULL,
        expected_state_version INT NULL,
        admit BIT NOT NULL,
        envelope NVARCHAR(MAX) NULL,
        job_ref UNIQUEIDENTIFIER NULL,
        deduplication_key VARCHAR(128) NULL,
        trigger_message NVARCHAR(512) NULL,
        trigger_details NVARCHAR(MAX) NULL);
    DECLARE @rows TABLE (
        schedule_id INT NOT NULL PRIMARY KEY,
        group_ordinal INT NOT NULL,
        schedule_name VARCHAR(128) NOT NULL,
        expected_version INT NOT NULL,
        next_occurrence_at_utc DATETIME2(3) NULL,
        next_evaluation_at_utc DATETIME2(3) NULL,
        evaluation_status_code TINYINT NOT NULL,
        pause_ended BIT NOT NULL,
        admitted_details NVARCHAR(MAX) NULL,
        skipped_details NVARCHAR(MAX) NULL,
        expired_details NVARCHAR(MAX) NULL,
        expire_paused BIT NOT NULL);
    DECLARE @slots TABLE (job_id BIGINT NOT NULL PRIMARY KEY, valid BIT NOT NULL);
    DECLARE @states TABLE (schedule_id INT NOT NULL PRIMARY KEY, version INT NOT NULL);
    DECLARE @valid TABLE (ordinal INT NOT NULL PRIMARY KEY);

    INSERT INTO @groups (
        ordinal, job_id, expected_job_version, schedule_id, expected_state_version, admit, envelope,
        job_ref, deduplication_key, trigger_message, trigger_details)
    SELECT
        ordinal, job_id, expected_job_version, schedule_id, expected_state_version, admit, envelope,
        job_ref, deduplication_key, trigger_message, trigger_details
    FROM OPENJSON(@p_groups) WITH (
        ordinal INT '$.ordinal', job_id BIGINT '$.job_id', expected_job_version INT '$.expected_job_version',
        schedule_id INT '$.schedule_id', expected_state_version INT '$.expected_state_version', admit BIT '$.admit',
        envelope NVARCHAR(MAX) '$.envelope', job_ref UNIQUEIDENTIFIER '$.job_ref', deduplication_key VARCHAR(128) '$.deduplication_key',
        trigger_message NVARCHAR(512) '$.trigger_message', trigger_details NVARCHAR(MAX) '$.trigger_details');

    INSERT INTO @rows (
        schedule_id, group_ordinal, schedule_name, expected_version,
        next_occurrence_at_utc, next_evaluation_at_utc, evaluation_status_code,
        pause_ended, admitted_details, skipped_details, expired_details, expire_paused)
    SELECT
        schedule_id, group_ordinal, schedule_name, expected_version,
        next_occurrence_at_utc, next_evaluation_at_utc, evaluation_status_code,
        pause_ended, admitted_details, skipped_details, expired_details, expire_paused
    FROM OPENJSON(@p_rows) WITH (
        schedule_id INT '$.schedule_id', group_ordinal INT '$.group_ordinal', schedule_name VARCHAR(128) '$.schedule_name',
        expected_version INT '$.expected_version',
        next_occurrence_at_utc DATETIME2(3) '$.next_occurrence_at_utc',
        next_evaluation_at_utc DATETIME2(3) '$.next_evaluation_at_utc', evaluation_status_code TINYINT '$.evaluation_status_code',
        pause_ended BIT '$.pause_ended', admitted_details NVARCHAR(MAX) '$.admitted_details', skipped_details NVARCHAR(MAX) '$.skipped_details',
        expired_details NVARCHAR(MAX) '$.expired_details', expire_paused BIT '$.expire_paused');

    -- The namespace's schedule lock, shared, before any row lock (docs/sql-internals.md, "Schedule lock").
    EXEC @lock_result = sys.sp_getapplock @lock_resource, 'Shared', 'Transaction', -1;
    IF @lock_result < 0
        THROW 51205, 'ACTA:SCHEDULE_LOCK:The namespace schedule lock was not granted (deadlock victim or cancelled).', 1;

    -- Slot rows in id order, then every schedule state the plan touches, a member's or a per-occurrence group's own,
    -- in id order, each a key seek under an update lock. An admitting slot group needs the slot Idle at the version it
    -- read; a cursor-only group needs only the version.
    INSERT INTO @slots (job_id, valid)
    SELECT
        g.job_id,
        CASE WHEN r.version = g.expected_job_version AND (g.admit = 0 OR r.job_status_code = 40 /* JobStatusCode.Idle */) THEN 1 ELSE 0 END
    FROM @groups g
    INNER JOIN {{schema}}.jobs_state r WITH (UPDLOCK, ROWLOCK, FORCESEEK) ON r.job_id = g.job_id
    WHERE g.job_id IS NOT NULL;

    INSERT INTO @states (schedule_id, version)
    SELECT st.schedule_id, st.version
    FROM (
        SELECT a.schedule_id FROM @rows a
        UNION
        SELECT g.schedule_id FROM @groups g WHERE g.schedule_id IS NOT NULL
    ) k
    INNER JOIN {{schema}}.schedules_state st WITH (UPDLOCK, ROWLOCK, FORCESEEK) ON st.schedule_id = k.schedule_id;

    -- A group applies whole or not at all: a slot, a member, or a per-occurrence schedule's own state another writer
    -- moved since the plan read it replans; the state version is what every settlement bumps, so the capacity the plan
    -- read still holds.
    INSERT INTO @valid (ordinal)
    SELECT g.ordinal
    FROM @groups g
    LEFT JOIN @slots s ON s.job_id = g.job_id
    LEFT JOIN @states gs ON gs.schedule_id = g.schedule_id
    WHERE
        (
            (g.job_id IS NOT NULL AND s.valid = 1)
            OR (g.job_id IS NULL AND gs.version = g.expected_state_version)
        )
        AND NOT EXISTS (
            SELECT 1
            FROM @rows a
            INNER JOIN @states x ON x.schedule_id = a.schedule_id
            WHERE a.group_ordinal = g.ordinal AND x.version <> a.expected_version
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
    INNER JOIN @valid v ON v.ordinal = g.ordinal
    WHERE g.admit = 1;

    -- A per-occurrence firing is a fresh root: the definition's stored default input, priority, and audit level, the
    -- schedule as its provenance, no lane and no tenant, keyed so the same occurrence is never admitted twice. Ready at
    -- once. Identity values follow the plan's order.
    INSERT INTO {{schema}}.jobs (
        job_kind_code, job_ref, deduplication_key, namespace_id, definition_id, schedule_id,
        input_format_id, input, event_recording_code, created_at_utc)
    SELECT
        10 /* JobKindCode.Ordinary */, g.job_ref, g.deduplication_key, @p_namespace_id, js.definition_id, g.schedule_id,
        jd.default_input_format_id, jd.default_input, jd.event_recording_code_effective, @now
    FROM @groups g
    INNER JOIN @valid v ON v.ordinal = g.ordinal
    INNER JOIN {{schema}}.schedules js WITH (FORCESEEK) ON js.schedule_id = g.schedule_id
    INNER JOIN {{schema}}.definitions jd WITH (FORCESEEK) ON jd.definition_id = js.definition_id
    WHERE g.admit = 1 AND g.job_id IS NULL
    ORDER BY g.ordinal;

    INSERT INTO {{schema}}.jobs_state (
        job_id, namespace_id, lane_id, job_status_code, job_priority_code, next_run_at_utc,
        execution_number, failure_count, retention_until_utc, modified_at_utc, version)
    SELECT
        j.job_id, j.namespace_id, NULL, 10 /* JobStatusCode.Ready */, jd.job_priority_code_effective, @now,
        0, 0, NULL, @now, 0
    FROM @groups g
    INNER JOIN @valid v ON v.ordinal = g.ordinal
    INNER JOIN {{schema}}.jobs j WITH (FORCESEEK) ON j.job_ref = g.job_ref
    INNER JOIN {{schema}}.definitions jd WITH (FORCESEEK) ON jd.definition_id = j.definition_id
    WHERE g.admit = 1 AND g.job_id IS NULL
    OPTION (LOOP JOIN);

    -- The envelope: on a slot it replaces the settled one of the previous firing, since a Pending one never exists on
    -- an Idle slot; on a root it is the first. The JSON is ASCII by construction, so the VARCHAR cast is its UTF-8.
    UPDATE cp
    SET
        checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */,
        value_format_id = 1 /* JobPayloadFormat.Json */,
        value = CAST(CAST(g.envelope AS VARCHAR(MAX)) AS VARBINARY(MAX)),
        modified_at_utc = @now,
        version = cp.version + 1
    FROM {{schema}}.checkpoints cp WITH (FORCESEEK)
    INNER JOIN @groups g ON g.job_id = cp.job_id
    INNER JOIN @valid v ON v.ordinal = g.ordinal
    WHERE
        g.admit = 1
        AND cp.checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */
        AND cp.checkpoint_name = 'sys.firing';

    INSERT INTO {{schema}}.checkpoints (
        job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code,
        value_format_id, value, created_at_utc, modified_at_utc, version)
    SELECT
        COALESCE(g.job_id, root.job_id), 60 /* CheckpointKindCode.Firing */, 'sys.firing', 10 /* CheckpointStatusCode.Pending */,
        1 /* JobPayloadFormat.Json */, CAST(CAST(g.envelope AS VARCHAR(MAX)) AS VARBINARY(MAX)), @now, @now, 0
    FROM @groups g
    INNER JOIN @valid v ON v.ordinal = g.ordinal
    LEFT JOIN {{schema}}.jobs root WITH (FORCESEEK) ON root.job_ref = g.job_ref
    WHERE
        g.admit = 1
        AND NOT EXISTS (
            SELECT 1
            FROM {{schema}}.checkpoints cp
            WHERE
                cp.job_id = COALESCE(g.job_id, root.job_id)
                AND cp.checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */
                AND cp.checkpoint_name = 'sys.firing'
        );

    -- The cursors move, and an admitted root is charged to its schedule.
    UPDATE st
    SET
        next_occurrence_at_utc = a.next_occurrence_at_utc,
        next_evaluation_at_utc = a.next_evaluation_at_utc,
        evaluation_status_code = a.evaluation_status_code,
        outstanding_count = st.outstanding_count + CASE WHEN g.admit = 1 AND g.job_id IS NULL THEN 1 ELSE 0 END,
        modified_at_utc = @now,
        version = st.version + 1
    FROM {{schema}}.schedules_state st WITH (FORCESEEK)
    INNER JOIN @rows a ON a.schedule_id = st.schedule_id
    INNER JOIN @valid v ON v.ordinal = a.group_ordinal
    INNER JOIN @groups g ON g.ordinal = a.group_ordinal;

    -- A manual root moves no cursor; it is charged on its own.
    UPDATE st
    SET
        outstanding_count = st.outstanding_count + 1,
        modified_at_utc = @now,
        version = st.version + 1
    FROM {{schema}}.schedules_state st WITH (FORCESEEK)
    INNER JOIN @groups g ON g.schedule_id = st.schedule_id
    INNER JOIN @valid v ON v.ordinal = g.ordinal
    WHERE
        g.admit = 1
        AND g.job_id IS NULL
        AND NOT EXISTS (SELECT 1 FROM @rows a WHERE a.group_ordinal = g.ordinal);

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
    INNER JOIN @valid v ON v.ordinal = a.group_ordinal
    WHERE a.pause_ended = 1 AND js.schedule_status_code = 30 /* ScheduleStatusCode.Paused */;

    -- An occurrence that expired unadmitted under the Pause action stops the schedule for an operator.
    UPDATE js
    SET
        schedule_status_code = 30 /* ScheduleStatusCode.Paused */,
        paused_until_utc = NULL,
        pause_message = 'Paused by the expiration policy: an occurrence expired unadmitted.',
        modified_at_utc = @now,
        version = js.version + 1
    FROM {{schema}}.schedules js WITH (FORCESEEK)
    INNER JOIN @rows a ON a.schedule_id = js.schedule_id
    INNER JOIN @valid v ON v.ordinal = a.group_ordinal
    WHERE a.expire_paused = 1 AND js.schedule_status_code = 10 /* ScheduleStatusCode.Active */;

    -- Decision evidence at Audit: an expired range, a skipped range, an admission, an elapsed pause, an expiry pause, a
    -- manual trigger. A slot's decisions hang on the slot; a per-occurrence admission or trigger on its root, the rest
    -- of a per-occurrence schedule's on the definition. ASCII names and JSON, so the VARCHAR cast is their UTF-8.
    INSERT INTO {{schema}}.events (
        event_code, created_at_utc, namespace_id,
        entity_code, entity_id,
        definition_id, tenant_id, lineage_root_job_id,
        execution_number,
        event_message, details_format_id, details
    )
    SELECT
        k.event_code, @now, @p_namespace_id,
        CASE WHEN k.on_job IS NULL THEN 30 /* EntityCode.Definition */ ELSE 50 /* EntityCode.Job */ END,
        COALESCE(k.on_job, k.definition_id),
        k.definition_id, k.tenant_id, k.lineage_root_job_id,
        k.execution_number,
        k.event_message, 1 /* JobPayloadFormat.Json */, CAST(CAST(k.details AS VARCHAR(MAX)) AS VARBINARY(MAX))
    FROM (
        SELECT
            x.schedule_id, x.ord, x.event_code, x.details, x.event_message,
            CASE WHEN g.job_id IS NOT NULL THEN g.job_id WHEN x.ord IN (3, 6) THEN root.job_id END AS on_job,
            js.definition_id, j.tenant_id,
            COALESCE(j.lineage_root_job_id, j.job_id, root.job_id) AS lineage_root_job_id,
            COALESCE(r.execution_number, 0) AS execution_number,
            COALESCE(j.event_recording_code, jd.event_recording_code_effective) AS event_recording_code
        FROM (
            SELECT a.group_ordinal, a.schedule_id, 1 AS ord, 106 /* EventCode.ScheduleSkipped */ AS event_code, a.expired_details AS details, CAST(NULL AS NVARCHAR(512)) AS event_message
            FROM @rows a
            UNION ALL
            SELECT a.group_ordinal, a.schedule_id, 2, 106 /* EventCode.ScheduleSkipped */, a.skipped_details, NULL
            FROM @rows a
            UNION ALL
            SELECT a.group_ordinal, a.schedule_id, 3, 105 /* EventCode.ScheduleAdmitted */, a.admitted_details, NULL
            FROM @rows a
            UNION ALL
            SELECT a.group_ordinal, a.schedule_id, 4, 102 /* EventCode.SchedulePauseExpired */, N'{"schedule":"' + a.schedule_name + N'"}', NULL
            FROM @rows a
            WHERE a.pause_ended = 1
            UNION ALL
            SELECT a.group_ordinal, a.schedule_id, 5, 100 /* EventCode.SchedulePaused */, N'{"schedule":"' + a.schedule_name + N'","reason":"expired"}', NULL
            FROM @rows a
            WHERE a.expire_paused = 1
            UNION ALL
            SELECT g.ordinal, g.schedule_id, 6, 104 /* EventCode.ScheduleTriggered */, g.trigger_details, g.trigger_message
            FROM @groups g
            WHERE g.job_id IS NULL AND g.admit = 1 AND g.trigger_details IS NOT NULL
        ) x
        INNER JOIN @valid v ON v.ordinal = x.group_ordinal
        INNER JOIN @groups g ON g.ordinal = x.group_ordinal
        INNER JOIN {{schema}}.schedules js ON js.schedule_id = x.schedule_id
        INNER JOIN {{schema}}.definitions jd ON jd.definition_id = js.definition_id
        LEFT JOIN {{schema}}.jobs j ON j.job_id = g.job_id
        LEFT JOIN {{schema}}.jobs_state r ON r.job_id = g.job_id
        LEFT JOIN {{schema}}.jobs root ON root.job_ref = g.job_ref
    ) k
    WHERE k.event_recording_code = 20 /* EventRecordingCode.Audit */ AND k.details IS NOT NULL
    ORDER BY k.schedule_id, k.ord;

    SELECT g.ordinal, CAST(CASE WHEN v.ordinal IS NULL THEN 0 ELSE 1 END AS BIT) AS applied
    FROM @groups g
    LEFT JOIN @valid v ON v.ordinal = g.ordinal;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
