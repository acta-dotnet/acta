SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @lock_result INT;
    DECLARE @lock_resource NVARCHAR(255) = N'{{schema}}.schedules.' + CAST(@p_namespace_id AS NVARCHAR(12));

    -- The namespace's schedule lock, exclusive, before any row lock (docs/sql-internals.md, "Schedule lock").
    EXEC @lock_result = sys.sp_getapplock @lock_resource, 'Exclusive', 'Transaction', -1;
    IF @lock_result < 0
        THROW 51205, 'ACTA:SCHEDULE_LOCK:The namespace schedule lock was not granted (deadlock victim or cancelled).', 1;

    UPDATE j
    SET
        input_format_id = d.input_format_id,
        input = d.input,
        event_recording_code = d.event_recording_code
    FROM {{schema}}.jobs AS j
    INNER JOIN @p_definitions AS d ON d.definition_id = j.definition_id
    WHERE j.job_kind_code = 20 /* JobKindCode.PermanentSlot */;

    -- The declaration's priority reaches a slot between firings; one an attempt or an operator holds keeps its own.
    UPDATE r
    SET
        job_priority_code = jd.job_priority_code_effective,
        modified_at_utc = @now,
        version = r.version + 1
    FROM {{schema}}.jobs_state AS r
    INNER JOIN {{schema}}.jobs AS j ON j.job_id = r.job_id
    INNER JOIN @p_definitions AS d ON d.definition_id = j.definition_id
    INNER JOIN {{schema}}.definitions AS jd ON jd.definition_id = d.definition_id
    WHERE
        j.job_kind_code = 20 /* JobKindCode.PermanentSlot */
        AND r.job_status_code = 40 /* JobStatusCode.Idle */
        AND r.job_priority_code <> jd.job_priority_code_effective;

    INSERT INTO {{schema}}.jobs (
        job_kind_code, job_ref, lineage_root_job_id, parent_job_id, correlation_key,
        namespace_id, definition_id,
        input_format_id, input,
        concurrency_key, event_recording_code,
        created_at_utc
    )
    SELECT
        20 /* JobKindCode.PermanentSlot */,
        d.job_ref,
        NULL,
        NULL,
        NULL,
        @p_namespace_id,
        d.definition_id,
        d.input_format_id,
        d.input,
        NULL,
        d.event_recording_code,
        @now
    FROM @p_definitions AS d
    WHERE
        -- A PerOccurrence definition has no slot: each admitted occurrence is a root of its own.
        d.scheduled_delivery_code = 10 /* ScheduledDeliveryCode.Permanent */
        AND NOT EXISTS (
            SELECT 1
            FROM {{schema}}.jobs AS j
            WHERE j.definition_id = d.definition_id AND j.job_kind_code = 20 /* JobKindCode.PermanentSlot */
        );

    INSERT INTO {{schema}}.jobs_state (
        job_id, namespace_id, job_status_code, job_priority_code, next_run_at_utc,
        execution_number, failure_count, retention_until_utc,
        modified_at_utc, version
    )
    SELECT
        j.job_id,
        @p_namespace_id,
        40 /* JobStatusCode.Idle */,
        jd.job_priority_code_effective,
        NULL,
        0,
        0,
        NULL,
        @now,
        0
    FROM @p_definitions AS d
    INNER JOIN {{schema}}.jobs AS j
        ON
            j.definition_id = d.definition_id
            AND j.job_kind_code = 20 /* JobKindCode.PermanentSlot */
    INNER JOIN {{schema}}.definitions AS jd ON jd.definition_id = d.definition_id
    WHERE NOT EXISTS (
        SELECT 1 FROM {{schema}}.jobs_state AS r
        WHERE r.job_id = j.job_id
    );

    DECLARE @slots TABLE (definition_id INT NOT NULL PRIMARY KEY, slot_id BIGINT NOT NULL);

    -- The definition's slot, inserted by this call or found by its kind.
    INSERT INTO @slots (definition_id, slot_id)
    SELECT
        d.definition_id,
        j.job_id
    FROM @p_definitions AS d
    INNER JOIN {{schema}}.jobs AS j
        ON
            j.definition_id = d.definition_id
            AND j.job_kind_code = 20 /* JobKindCode.PermanentSlot */;

    -- The schedule writes seek by key (docs/sql-internals.md, "Locking reads seek").
    /* A schedule whose timing changed is retimed: the old stream's undecided occurrences are discarded and the plan's
       cursor is the new timing's first occurrence. Written before the declaration, which overwrites the fingerprint the
       retime is decided by. A schedule name is dotted kebab ASCII. */
    INSERT INTO {{schema}}.events (
        event_code, created_at_utc, namespace_id,
        entity_code, entity_id,
        definition_id, tenant_id, lineage_root_job_id,
        execution_number,
        details_format_id, details
    )
    SELECT
        107 /* EventCode.ScheduleRetimed */, @now, @p_namespace_id,
        50 /* EntityCode.Job */, s.job_id,
        s.definition_id, j.tenant_id, COALESCE(j.lineage_root_job_id, j.job_id),
        r.execution_number,
        1 /* JobPayloadFormat.Json */, CAST('{"schedule":"' + s.schedule_name + '"}' AS VARBINARY(MAX))
    FROM @p_schedules AS src
    INNER JOIN {{schema}}.schedules AS s ON s.definition_id = src.definition_id AND s.schedule_name = src.schedule_name
    INNER JOIN {{schema}}.jobs AS j ON j.job_id = s.job_id
    INNER JOIN {{schema}}.jobs_state AS r ON r.job_id = s.job_id
    WHERE s.timing_fingerprint <> src.timing_fingerprint AND j.event_recording_code = 20 /* EventRecordingCode.Audit */;

    -- A schedule with no slot records its retime against its definition.
    INSERT INTO {{schema}}.events (
        event_code, created_at_utc, namespace_id,
        entity_code, entity_id,
        definition_id,
        details_format_id, details
    )
    SELECT
        107 /* EventCode.ScheduleRetimed */, @now, @p_namespace_id,
        30 /* EntityCode.Definition */, s.definition_id,
        s.definition_id,
        1 /* JobPayloadFormat.Json */, CAST('{"schedule":"' + s.schedule_name + '"}' AS VARBINARY(MAX))
    FROM @p_schedules AS src
    INNER JOIN {{schema}}.schedules AS s ON s.definition_id = src.definition_id AND s.schedule_name = src.schedule_name
    INNER JOIN {{schema}}.definitions AS d ON d.definition_id = s.definition_id
    WHERE
        s.job_id IS NULL
        AND s.timing_fingerprint <> src.timing_fingerprint
        AND d.event_recording_code_effective = 20 /* EventRecordingCode.Audit */;

    -- The cursor is written on a retime, from the plan, and seeded when the stored one was cleared by the orphaning or the
    -- retire the schedule now comes back from. A cursor the plan copied from the stored one is never written back: the
    -- plan read it unlocked, and another host's admission may have moved it since.
    UPDATE st
    SET
        next_occurrence_at_utc = src.next_occurrence_at_utc,
        next_evaluation_at_utc = CASE WHEN s.schedule_status_code = 30 /* ScheduleStatusCode.Paused */ THEN s.paused_until_utc ELSE src.next_occurrence_at_utc END,
        evaluation_status_code = CASE WHEN src.next_occurrence_at_utc IS NULL THEN 30 /* EvaluationStatusCode.Exhausted */ ELSE 10 /* EvaluationStatusCode.Scheduled */ END,
        version = st.version + 1,
        modified_at_utc = @now
    FROM {{schema}}.schedules_state AS st WITH (FORCESEEK)
    INNER JOIN {{schema}}.schedules AS s ON s.schedule_id = st.schedule_id
    INNER JOIN @p_schedules AS src
        ON
            src.definition_id = s.definition_id
            AND src.schedule_name = s.schedule_name
    WHERE
        s.timing_fingerprint <> src.timing_fingerprint
        OR (st.next_occurrence_at_utc IS NULL AND src.next_occurrence_at_utc IS NOT NULL);

    -- A schedule parked at its declared capacity waits for a settlement; a declaration that changes the capacity wakes it
    -- instead, so the admitter re-plans against the new one. Written before the declaration overwrites the capacity.
    UPDATE st
    SET
        next_evaluation_at_utc = @now,
        evaluation_status_code = 10 /* EvaluationStatusCode.Scheduled */,
        version = st.version + 1,
        modified_at_utc = @now
    FROM {{schema}}.schedules_state AS st WITH (FORCESEEK)
    INNER JOIN {{schema}}.schedules AS s ON s.schedule_id = st.schedule_id
    INNER JOIN @p_schedules AS src
        ON
            src.definition_id = s.definition_id
            AND src.schedule_name = s.schedule_name
    WHERE
        st.evaluation_status_code = 20 /* EvaluationStatusCode.WaitingForCapacity */
        AND s.schedule_status_code = 10 /* ScheduleStatusCode.Active */
        AND (s.max_outstanding <> src.max_outstanding OR s.full_behavior_code <> src.full_behavior_code);

    UPDATE tgt
    SET
        expression = src.expression,
        time_zone_id = src.time_zone_id,
        expression_kind_code = src.expression_kind_code,
        misfire_strategy_code = src.misfire_strategy_code,
        interval_anchor_at_utc = src.interval_anchor_at_utc,
        timing_fingerprint = src.timing_fingerprint,
        max_outstanding = src.max_outstanding,
        full_behavior_code = src.full_behavior_code,
        expire_after_seconds = src.expire_after_seconds,
        expire_action_code = src.expire_action_code,
        availability_calendar_name = src.availability_calendar_name,
        schedule_status_code = CASE
            WHEN tgt.schedule_status_code = 230 /* ScheduleStatusCode.Orphaned */
                THEN 10 /* ScheduleStatusCode.Active */
            ELSE tgt.schedule_status_code
        END,
        description = src.description,
        modified_at_utc = @now,
        version = tgt.version + 1
    FROM {{schema}}.schedules AS tgt WITH (FORCESEEK)
    INNER JOIN @p_schedules AS src
        ON
            src.definition_id = tgt.definition_id
            AND src.schedule_name = tgt.schedule_name
    INNER JOIN {{schema}}.schedules_state AS st ON st.schedule_id = tgt.schedule_id
    WHERE
        tgt.schedule_status_code = 230 /* ScheduleStatusCode.Orphaned */
        OR EXISTS (
            SELECT tgt.expression, tgt.time_zone_id, tgt.expression_kind_code, tgt.misfire_strategy_code,
                st.next_occurrence_at_utc, tgt.description, tgt.timing_fingerprint, tgt.max_outstanding, tgt.full_behavior_code,
                tgt.expire_after_seconds, tgt.expire_action_code, tgt.availability_calendar_name
            EXCEPT
            SELECT src.expression, src.time_zone_id, src.expression_kind_code, src.misfire_strategy_code,
                src.next_occurrence_at_utc, src.description, src.timing_fingerprint, src.max_outstanding, src.full_behavior_code,
                src.expire_after_seconds, src.expire_action_code, src.availability_calendar_name);

    INSERT INTO {{schema}}.schedules (
        namespace_id, job_id, definition_id, schedule_name, schedule_origin_code,
        expression, time_zone_id, expression_kind_code, misfire_strategy_code,
        interval_anchor_at_utc, misfire_grace_seconds, timing_fingerprint,
        max_outstanding, full_behavior_code, expire_after_seconds, expire_action_code,
        availability_calendar_name,
        schedule_status_code, paused_until_utc, description,
        created_at_utc, modified_at_utc, version
    )
    SELECT
        @p_namespace_id,
        sl.slot_id,
        src.definition_id,
        src.schedule_name,
        40 /* ScheduleOriginCode.Definition */,
        src.expression,
        src.time_zone_id,
        src.expression_kind_code,
        src.misfire_strategy_code,
        src.interval_anchor_at_utc,
        60,
        src.timing_fingerprint,
        src.max_outstanding,
        src.full_behavior_code,
        src.expire_after_seconds,
        src.expire_action_code,
        src.availability_calendar_name,
        10 /* ScheduleStatusCode.Active */,
        NULL,
        src.description,
        @now,
        @now,
        0
    FROM @p_schedules AS src
    -- A PerOccurrence definition's schedules have no slot, so their job_id is NULL.
    LEFT JOIN @slots AS sl ON sl.definition_id = src.definition_id
    WHERE NOT EXISTS (
        SELECT 1
        FROM {{schema}}.schedules AS tgt
        WHERE
            tgt.definition_id = src.definition_id
            AND tgt.schedule_name = src.schedule_name
    );

    INSERT INTO {{schema}}.schedules_state (
        schedule_id, namespace_id,
        next_occurrence_at_utc, next_evaluation_at_utc, evaluation_status_code, outstanding_count,
        version, modified_at_utc
    )
    SELECT
        s.schedule_id,
        @p_namespace_id,
        src.next_occurrence_at_utc,
        src.next_occurrence_at_utc,
        CASE WHEN src.next_occurrence_at_utc IS NULL THEN 30 /* EvaluationStatusCode.Exhausted */ ELSE 10 /* EvaluationStatusCode.Scheduled */ END,
        0,
        0,
        @now
    FROM @p_schedules AS src
    INNER JOIN {{schema}}.schedules AS s ON s.definition_id = src.definition_id AND s.schedule_name = src.schedule_name
    WHERE NOT EXISTS (
        SELECT 1
        FROM {{schema}}.schedules_state AS st
        WHERE st.schedule_id = s.schedule_id
    );

    DECLARE @candidates TABLE (schedule_id INT NOT NULL PRIMARY KEY);

    INSERT INTO @candidates (schedule_id)
    SELECT js.schedule_id
    FROM {{schema}}.schedules AS js
    INNER JOIN @p_definitions AS d ON d.definition_id = js.definition_id
    WHERE
        js.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
        AND js.schedule_origin_code = 40 /* ScheduleOriginCode.Definition */
        AND NOT EXISTS (
            SELECT 1
            FROM @p_schedules AS src
            WHERE
                src.definition_id = js.definition_id
                AND src.schedule_name = js.schedule_name
        );

    UPDATE js
    SET
        schedule_status_code = 230 /* ScheduleStatusCode.Orphaned */,
        paused_until_utc = NULL,
        pause_message = NULL,
        modified_at_utc = @now,
        version = js.version + 1
    FROM {{schema}}.schedules AS js WITH (FORCESEEK)
    INNER JOIN @candidates AS c ON c.schedule_id = js.schedule_id;

    UPDATE st
    SET
        next_occurrence_at_utc = NULL,

        next_evaluation_at_utc = NULL,

        evaluation_status_code = 30 /* EvaluationStatusCode.Exhausted */,

        version = st.version + 1,
        modified_at_utc = @now
    FROM {{schema}}.schedules_state AS st WITH (FORCESEEK)
    INNER JOIN @candidates AS c ON c.schedule_id = st.schedule_id;

    /* A retire that commits after the worker read its stored schedules leaves this call writing cursors for the
       retired definition's schedules; the heal clears every orphaned or retired schedule's cursor in the namespace. */
    UPDATE st
    SET
        next_occurrence_at_utc = NULL,

        next_evaluation_at_utc = NULL,

        evaluation_status_code = 30 /* EvaluationStatusCode.Exhausted */,

        version = st.version + 1,
        modified_at_utc = @now
    FROM {{schema}}.schedules_state AS st WITH (INDEX (ix_schedules_state_namespace_next), FORCESEEK)
    INNER JOIN {{schema}}.schedules AS s ON s.schedule_id = st.schedule_id
    INNER JOIN {{schema}}.definitions AS d ON d.definition_id = s.definition_id
    WHERE
        st.namespace_id = @p_namespace_id
        AND st.next_occurrence_at_utc IS NOT NULL
        AND (
            s.schedule_status_code = 230 /* ScheduleStatusCode.Orphaned */
            OR d.definition_status_code = 240 /* DefinitionStatusCode.Retired */
        );

    -- The catalog this call registered; a start that computes the same hash and moves no cursor skips the call.
    UPDATE n
    SET catalog_hash = @p_catalog_hash
    FROM {{schema}}.namespaces AS n
    WHERE
        n.namespace_id = @p_namespace_id
        AND EXISTS (SELECT n.catalog_hash EXCEPT SELECT @p_catalog_hash);

    SELECT
        definition_id,
        slot_id
    FROM @slots;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
