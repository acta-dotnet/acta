SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @lock_result INT;
    DECLARE @lock_resource NVARCHAR(255) = N'{{schema}}.schedules.' + CAST(@p_namespace_id AS NVARCHAR(12));

    -- The namespace's schedule lock, exclusive, before any row lock (docs/sql-execution-policy.md, "Schedule lock").
    EXEC @lock_result = sys.sp_getapplock @lock_resource, 'Exclusive', 'Transaction', -1;
    IF @lock_result < 0
        THROW 51205, 'ACTA:SCHEDULE_LOCK:The namespace schedule lock was not granted (deadlock victim or cancelled).', 1;

    -- Each slot's key lock, named as enqueue_job names it, so an enqueue of a slot's key and this insert wait for
    -- each other (docs/sql-execution-policy.md, "Deduplication lock").
    DECLARE @dedup_locks TABLE (name NVARCHAR(255) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY);
    INSERT INTO @dedup_locks (name)
    SELECT DISTINCT CONCAT(N'{{schema}}.dedup:n', @p_namespace_id, N':', d.deduplication_key)
    FROM @p_definitions AS d
    WHERE d.deduplication_key IS NOT NULL;

    DECLARE @dedup_lock NVARCHAR(255) = (SELECT MIN(name) FROM @dedup_locks);
    WHILE @dedup_lock IS NOT NULL
        BEGIN
            EXEC @lock_result = sys.sp_getapplock @dedup_lock, 'Exclusive', 'Transaction', -1;
            IF @lock_result < 0
                THROW 51205, 'ACTA:DEDUP_LOCK:The deduplication key lock was not granted (deadlock victim or cancelled).', 1;
            SET @dedup_lock = (SELECT MIN(name) FROM @dedup_locks WHERE name > @dedup_lock);
        END;

    UPDATE j
    SET
        input_format_id = d.input_format_id,
        input = d.input,
        event_recording_code = d.event_recording_code
    FROM {{schema}}.jobs AS j
    INNER JOIN @p_definitions AS d ON d.deduplication_key = j.deduplication_key
    WHERE
        j.namespace_id = @p_namespace_id
        AND j.parent_job_id IS NULL
        -- A row already holding the key is the slot only when it owns schedules; an ordinary job that
        -- took the key is left untouched and missing from the result, which fails the worker's startup.
        AND EXISTS (SELECT 1 FROM {{schema}}.schedules AS s WHERE s.job_id = j.job_id);

    UPDATE r
    SET
        job_status_code = d.slot_status_code,
        job_priority_code = jd.job_priority_code_effective,
        next_run_at_utc = d.slot_next_run_at_utc,
        modified_at_utc = @now,
        version = r.version + 1
    FROM {{schema}}.jobs_state AS r
    INNER JOIN {{schema}}.jobs AS j ON j.job_id = r.job_id
    INNER JOIN @p_definitions AS d ON d.deduplication_key = j.deduplication_key
    INNER JOIN {{schema}}.definitions AS jd ON jd.definition_id = d.definition_id
    WHERE
        j.namespace_id = @p_namespace_id
        AND j.parent_job_id IS NULL
        AND EXISTS (SELECT 1 FROM {{schema}}.schedules AS s WHERE s.job_id = j.job_id)
        -- An attempt in flight owns the slot, and a finished slot stays finished until an operator restarts it.
        AND r.job_status_code NOT IN (
            50 /* JobStatusCode.Executing */,
            100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
        -- A held job (Paused with a next run; ScheduleWalker, "held") stays as its operator left it.
        AND NOT (r.job_status_code = 30 /* JobStatusCode.Paused */ AND r.next_run_at_utc IS NOT NULL)
        -- A wait is left to the signal, child, or deadline that ends it, and a start never parks a slot: one a
        -- signal woke after the start read it would otherwise go back to sleep.
        AND r.job_status_code <> 20 /* JobStatusCode.Suspended */
        AND d.slot_status_code <> 20 /* JobStatusCode.Suspended */
        -- An unchanged declaration writes nothing: a restart of the same build bumps no version.
        -- EXCEPT compares NULL-safely, which <> does not.
        AND EXISTS (
            SELECT r.job_status_code, r.job_priority_code, r.next_run_at_utc
            EXCEPT
            SELECT d.slot_status_code, jd.job_priority_code_effective, d.slot_next_run_at_utc);

    INSERT INTO {{schema}}.jobs (
        job_ref, lineage_root_job_id, parent_job_id, deduplication_key, correlation_key,
        namespace_id, definition_id,
        input_format_id, input,
        concurrency_key, event_recording_code,
        created_at_utc
    )
    SELECT
        d.job_ref,
        NULL,
        NULL,
        d.deduplication_key,
        NULL,
        @p_namespace_id,
        d.definition_id,
        d.input_format_id,
        d.input,
        NULL,
        d.event_recording_code,
        @now
    FROM @p_definitions AS d
    WHERE NOT EXISTS (
        SELECT 1
        FROM {{schema}}.jobs AS j
        WHERE
            j.namespace_id = @p_namespace_id
            AND j.deduplication_key = d.deduplication_key
            AND j.parent_job_id IS NULL
    );

    INSERT INTO {{schema}}.jobs_state (
        job_id, namespace_id, job_status_code, job_priority_code, next_run_at_utc,
        execution_number, failure_count, retention_until_utc,
        modified_at_utc, version
    )
    SELECT
        j.job_id,
        @p_namespace_id,
        d.slot_status_code,
        jd.job_priority_code_effective,
        d.slot_next_run_at_utc,
        0,
        0,
        NULL,
        @now,
        0
    FROM @p_definitions AS d
    INNER JOIN {{schema}}.jobs AS j
        ON
            j.namespace_id = @p_namespace_id
            AND j.deduplication_key = d.deduplication_key
            AND j.parent_job_id IS NULL
    INNER JOIN {{schema}}.definitions AS jd ON jd.definition_id = d.definition_id
    WHERE NOT EXISTS (
        SELECT 1 FROM {{schema}}.jobs_state AS r
        WHERE r.job_id = j.job_id
    );

    DECLARE @slots TABLE (definition_id INT NOT NULL PRIMARY KEY, slot_id BIGINT NOT NULL);

    INSERT INTO @slots (definition_id, slot_id)
    SELECT
        d.definition_id,
        j.job_id
    FROM @p_definitions AS d
    INNER JOIN {{schema}}.jobs AS j
        ON
            j.namespace_id = @p_namespace_id
            AND j.deduplication_key = d.deduplication_key
            AND j.parent_job_id IS NULL
    -- The slot this call inserted, or one that already owns schedules.
    WHERE j.job_ref = d.job_ref OR EXISTS (SELECT 1 FROM {{schema}}.schedules AS s WHERE s.job_id = j.job_id);

    -- The schedule writes seek by key (docs/sql-execution-policy.md, "Locking reads seek").
    UPDATE tgt
    SET
        expression = src.expression,
        time_zone_id = src.time_zone_id,
        expression_kind_code = src.expression_kind_code,
        -- An override was validated against the kind it was set for; one left under the other kind cannot parse.
        expression_override = CASE
            WHEN tgt.expression_kind_code <> src.expression_kind_code THEN NULL
            ELSE tgt.expression_override
        END,
        misfire_strategy_code = src.misfire_strategy_code,
        definition_id = src.definition_id,
        schedule_status_code = CASE
            WHEN tgt.schedule_status_code = 230 /* ScheduleStatusCode.Orphaned */
                THEN 10 /* ScheduleStatusCode.Active */
            ELSE tgt.schedule_status_code
        END,
        description = src.description,
        modified_at_utc = @now,
        version = tgt.version + 1
    FROM {{schema}}.schedules AS tgt WITH (FORCESEEK)
    INNER JOIN @slots AS sl ON sl.slot_id = tgt.job_id
    INNER JOIN @p_schedules AS src
        ON
            src.definition_id = sl.definition_id
            AND src.schedule_name = tgt.schedule_name
    INNER JOIN {{schema}}.schedules_state AS st ON st.job_id = tgt.job_id AND st.schedule_name = tgt.schedule_name
    WHERE
        tgt.schedule_status_code = 230 /* ScheduleStatusCode.Orphaned */
        OR EXISTS (
            SELECT tgt.expression, tgt.time_zone_id, tgt.expression_kind_code, tgt.misfire_strategy_code,
                st.next_occurrence_at_utc, tgt.definition_id, tgt.description
            EXCEPT
            SELECT src.expression, src.time_zone_id, src.expression_kind_code, src.misfire_strategy_code,
                src.next_occurrence_at_utc, src.definition_id, src.description);

    UPDATE st
    SET
        next_occurrence_at_utc = src.next_occurrence_at_utc,
        modified_at_utc = @now
    FROM {{schema}}.schedules_state AS st WITH (FORCESEEK)
    INNER JOIN @slots AS sl ON sl.slot_id = st.job_id
    INNER JOIN @p_schedules AS src
        ON
            src.definition_id = sl.definition_id
            AND src.schedule_name = st.schedule_name
    WHERE EXISTS (SELECT st.next_occurrence_at_utc EXCEPT SELECT src.next_occurrence_at_utc);

    INSERT INTO {{schema}}.schedules (
        namespace_id, job_id, definition_id, schedule_name, schedule_origin_code,
        expression, time_zone_id, expression_kind_code, misfire_strategy_code,
        expression_override, time_zone_id_override,
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
        NULL,
        NULL,
        10 /* ScheduleStatusCode.Active */,
        NULL,
        src.description,
        @now,
        @now,
        0
    FROM @p_schedules AS src
    INNER JOIN @slots AS sl ON sl.definition_id = src.definition_id
    WHERE NOT EXISTS (
        SELECT 1
        FROM {{schema}}.schedules AS tgt
        WHERE
            tgt.job_id = sl.slot_id
            AND tgt.schedule_name = src.schedule_name
    );

    INSERT INTO {{schema}}.schedules_state (
        job_id, schedule_name, namespace_id,
        next_occurrence_at_utc, last_advanced_occurrence_at_utc, modified_at_utc
    )
    SELECT
        sl.slot_id,
        src.schedule_name,
        @p_namespace_id,
        src.next_occurrence_at_utc,
        NULL,
        @now
    FROM @p_schedules AS src
    INNER JOIN @slots AS sl ON sl.definition_id = src.definition_id
    WHERE NOT EXISTS (
        SELECT 1
        FROM {{schema}}.schedules_state AS st
        WHERE
            st.job_id = sl.slot_id
            AND st.schedule_name = src.schedule_name
    );

    DECLARE @candidates TABLE (
        job_id BIGINT NOT NULL,
        schedule_name VARCHAR(128) NOT NULL,
        PRIMARY KEY (job_id, schedule_name));

    INSERT INTO @candidates (job_id, schedule_name)
    SELECT js.job_id, js.schedule_name
    FROM {{schema}}.schedules AS js
    INNER JOIN @slots AS sl ON sl.slot_id = js.job_id
    WHERE
        js.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
        AND js.schedule_origin_code = 40 /* ScheduleOriginCode.Definition */
        AND NOT EXISTS (
            SELECT 1
            FROM @p_schedules AS src
            WHERE
                src.definition_id = sl.definition_id
                AND src.schedule_name = js.schedule_name
        );

    UPDATE js
    SET
        schedule_status_code = 230 /* ScheduleStatusCode.Orphaned */,
        paused_until_utc = NULL,
        modified_at_utc = @now,
        version = js.version + 1
    FROM {{schema}}.schedules AS js WITH (FORCESEEK)
    INNER JOIN @candidates AS c ON c.job_id = js.job_id AND c.schedule_name = js.schedule_name;

    UPDATE st
    SET
        next_occurrence_at_utc = NULL,
        last_advanced_occurrence_at_utc = NULL,
        modified_at_utc = @now
    FROM {{schema}}.schedules_state AS st WITH (FORCESEEK)
    INNER JOIN @candidates AS c ON c.job_id = st.job_id AND c.schedule_name = st.schedule_name;

    /* A retire that commits after the worker read its stored schedules leaves this call writing cursors for the
       retired definition's schedules; the heal clears every orphaned or retired schedule's cursor in the namespace. */
    UPDATE st
    SET
        next_occurrence_at_utc = NULL,
        last_advanced_occurrence_at_utc = NULL,
        modified_at_utc = @now
    FROM {{schema}}.schedules_state AS st WITH (INDEX (ix_schedules_state_namespace_next), FORCESEEK)
    INNER JOIN {{schema}}.schedules AS s ON s.job_id = st.job_id AND s.schedule_name = st.schedule_name
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
