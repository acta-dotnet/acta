SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
DECLARE @ns INT, @version INT, @existing_status TINYINT, @retention_seconds INT, @action TINYINT;
DECLARE @parked TABLE (
    job_id BIGINT NOT NULL PRIMARY KEY,
    parent_job_id BIGINT NULL,
    namespace_id INT NOT NULL,
    tenant_id INT NULL,
    definition_id INT NOT NULL,
    lineage_root_job_id BIGINT NOT NULL,
    event_recording_code TINYINT NOT NULL);
DECLARE @cancelled TABLE (
    job_id BIGINT NOT NULL PRIMARY KEY,
    execution_number INT NOT NULL);
DECLARE @lanes TABLE (lane_id BIGINT NOT NULL PRIMARY KEY);
DECLARE @heads TABLE (job_id BIGINT NOT NULL PRIMARY KEY);
DECLARE @lane_cursor BIGINT = 0, @lane_next BIGINT, @lane_locked BIGINT, @promoted INT = 0;
DECLARE @lock_result INT, @lock_resource NVARCHAR(255);

BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    -- The namespace's schedule lock, exclusive, before any row lock (docs/sql-internals.md, "Schedule lock").
    SELECT @lock_resource = N'{{schema}}.schedules.' + CAST(jd.namespace_id AS NVARCHAR(12))
    FROM {{schema}}.definitions jd
    WHERE jd.definition_id = @p_definition_id;

    IF @lock_resource IS NOT NULL
        BEGIN
            EXEC @lock_result = sys.sp_getapplock @lock_resource, 'Exclusive', 'Transaction', -1;
            IF @lock_result < 0
                THROW 51205, 'ACTA:SCHEDULE_LOCK:The namespace schedule lock was not granted (deadlock victim or cancelled).', 1;
        END

    -- The lanes of the definition's parked laned jobs, locked one row at a time in id order before any
    -- job row (docs/sql-internals.md, "Lane lock order").
    INSERT INTO @lanes (lane_id)
    SELECT DISTINCT r.lane_id
    FROM {{schema}}.jobs_state r
    INNER JOIN {{schema}}.jobs j ON j.job_id = r.job_id
    WHERE
        j.definition_id = @p_definition_id
        AND r.lane_id IS NOT NULL
        AND r.job_status_code IN (
            10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */, 30 /* JobStatusCode.Paused */, 40 /* JobStatusCode.Idle */
        );

    WHILE 1 = 1
    BEGIN
        SET @lane_next = NULL;
        SELECT TOP (1) @lane_next = l.lane_id
        FROM @lanes l
        WHERE l.lane_id > @lane_cursor
        ORDER BY l.lane_id;

        IF @lane_next IS NULL
            BREAK;

        SET @lane_locked = NULL;
        SELECT @lane_locked = l.lane_id
        FROM {{schema}}.lanes l WITH (UPDLOCK, ROWLOCK)
        WHERE l.lane_id = @lane_next;

        IF @lane_locked IS NULL
            DELETE FROM @lanes WHERE lane_id = @lane_next;
        SET @lane_cursor = @lane_next;
    END;

    SELECT
        @ns = jd.namespace_id,
        @version = jd.version,
        @existing_status = jd.definition_status_code,
        @retention_seconds = jd.retention_seconds_effective
    FROM {{schema}}.definitions jd WITH (UPDLOCK, ROWLOCK)
    WHERE jd.definition_id = @p_definition_id;

    SET @action = CASE
        WHEN @ns IS NULL THEN 2 /* DefinitionOverrideAction.NotFound */
        WHEN @version <> @p_version THEN 3 /* DefinitionOverrideAction.VersionConflict */
        ELSE 1 /* DefinitionOverrideAction.Applied */
    END;

    IF @action = 1 /* DefinitionOverrideAction.Applied */ AND @existing_status <> 240 /* DefinitionStatusCode.Retired */
    BEGIN
        UPDATE {{schema}}.definitions
        SET
            definition_status_code = 240 /* DefinitionStatusCode.Retired */,
            modified_at_utc = @now,
            version = version + 1
        WHERE definition_id = @p_definition_id;
        SET @version = @version + 1;

        /* The parked set is materialized first so the jobs_state update drives off a table variable and
           can seek pk_jobs_state, per the SQL Server join-update rule in sql-internals.md. */
        INSERT INTO @parked (
            job_id,
            parent_job_id,
            namespace_id,
            tenant_id,
            definition_id,
            lineage_root_job_id,
            event_recording_code)
        SELECT
            j.job_id,
            j.parent_job_id,
            j.namespace_id,
            j.tenant_id,
            j.definition_id,
            COALESCE(j.lineage_root_job_id, j.job_id),
            j.event_recording_code
        FROM {{schema}}.jobs j
        INNER JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
        WHERE
            j.definition_id = @p_definition_id
            AND r.job_status_code IN (
                10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */, 30 /* JobStatusCode.Paused */, 40 /* JobStatusCode.Idle */
            )
            -- A laned job is parked here only in a lane locked above.
            AND (r.lane_id IS NULL OR r.lane_id IN (SELECT l.lane_id FROM @lanes l));

        UPDATE r
        SET
            job_status_code = 220 /* JobStatusCode.Cancelled */,
            leased_by_worker_id = NULL,
            lease_expires_at_utc = NULL,
            retention_until_utc = DATEADD(SECOND, @retention_seconds, @now),
            modified_at_utc = @now,
            version = r.version + 1
        OUTPUT INSERTED.job_id, INSERTED.execution_number
            INTO @cancelled (job_id, execution_number)
        FROM {{schema}}.jobs_state r WITH (FORCESEEK)
        INNER JOIN @parked p ON p.job_id = r.job_id
        WHERE
            r.job_status_code IN (
                10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */, 30 /* JobStatusCode.Paused */, 40 /* JobStatusCode.Idle */
            );

        -- A retired slot's outstanding firing settles with it.
        UPDATE cp
        SET
            checkpoint_status_code = 130 /* CheckpointStatusCode.Cancelled */,
            modified_at_utc = @now,
            version = cp.version + 1
        FROM {{schema}}.checkpoints cp WITH (FORCESEEK)
        INNER JOIN @parked p ON p.job_id = cp.job_id
        WHERE
            cp.checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */
            AND cp.checkpoint_name = 'sys.firing'
            AND cp.checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */;

        UPDATE st
        SET
            next_occurrence_at_utc = NULL,

            decided_through_at_utc = NULL,

            next_evaluation_at_utc = NULL,

            evaluation_status_code = 30 /* EvaluationStatusCode.Exhausted */,

            version = st.version + 1,
            modified_at_utc = @now
        FROM {{schema}}.schedules s
        INNER JOIN {{schema}}.schedules_state st WITH (FORCESEEK) ON st.schedule_id = s.schedule_id
        WHERE s.definition_id = @p_definition_id;

        INSERT INTO {{schema}}.events (
            event_code,
            created_at_utc,
            namespace_id,
            entity_code,
            entity_id,
            definition_id,
            tenant_id,
            lineage_root_job_id,
            execution_number,
            event_message,
            details_format_id,
            details)
        SELECT
            70 /* EventCode.JobCancelled */,
            @now,
            p.namespace_id,
            50 /* EntityCode.Job */,
            p.job_id,
            p.definition_id,
            p.tenant_id,
            p.lineage_root_job_id,
            c.execution_number,
            @p_event_message,
            1 /* JobPayloadFormat.Json */,
            @p_job_details
        FROM @cancelled c
        INNER JOIN @parked p ON p.job_id = c.job_id
        WHERE p.event_recording_code = 20 /* EventRecordingCode.Audit */;

        INSERT INTO {{schema}}.events (
            event_code,
            created_at_utc,
            namespace_id,
            entity_code,
            entity_id,
            definition_id,
            event_message,
            details_format_id,
            details)
        VALUES (
            31 /* EventCode.JobDefinitionRetired */,
            @now,
            @ns,
            30 /* EntityCode.Definition */,
            @p_definition_id,
            @p_definition_id,
            @p_event_message,
            @p_details_format_id,
            @p_details);

        -- Once every parked member is cancelled, each locked lane hands on to its lowest-id unfinished
        -- member if that member is Blocked and no member still runs, so no retired member is promoted.
        INSERT INTO @heads (job_id)
        SELECT h.job_id
        FROM @lanes l
        CROSS APPLY (
            SELECT TOP (1) m.job_id, m.job_status_code
            FROM {{schema}}.jobs_state m WITH (INDEX (ix_jobs_state_lane), FORCESEEK)
            WHERE
                m.lane_id = l.lane_id
                AND m.lane_id IS NOT NULL
                AND m.job_status_code IN (
                    10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                    30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                )
            ORDER BY m.job_id
        ) h
        WHERE
            h.job_status_code = 15 /* JobStatusCode.Blocked */
            AND NOT EXISTS (
                SELECT 1
                FROM {{schema}}.jobs_state o WITH (INDEX (ix_jobs_state_lane), FORCESEEK)
                WHERE
                    o.lane_id = l.lane_id
                    AND o.lane_id IS NOT NULL
                    AND o.job_status_code IN (
                        10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                        50 /* JobStatusCode.Executing */
                    )
            );

        UPDATE r
        SET
            job_status_code = 10 /* JobStatusCode.Ready */,
            next_run_at_utc = CASE WHEN r.next_run_at_utc > @now THEN r.next_run_at_utc ELSE @now END,
            modified_at_utc = @now,
            version = r.version + 1
        FROM {{schema}}.jobs_state r WITH (FORCESEEK)
        INNER JOIN @heads h ON h.job_id = r.job_id
        WHERE r.job_status_code = 15 /* JobStatusCode.Blocked */;
        SET @promoted = @@ROWCOUNT;
    END;

    SELECT
        @action AS action,
        @version AS version,
        c.job_id,
        p.parent_job_id,
        CAST(CASE WHEN @promoted > 0 THEN 1 ELSE 0 END AS BIT) AS lane_promoted
    FROM @cancelled c
    INNER JOIN @parked p ON p.job_id = c.job_id
    UNION ALL
    SELECT
        @action AS action,
        @version AS version,
        CAST(NULL AS BIGINT) AS job_id,
        CAST(NULL AS BIGINT) AS parent_job_id,
        CAST(CASE WHEN @promoted > 0 THEN 1 ELSE 0 END AS BIT) AS lane_promoted
    WHERE NOT EXISTS (SELECT 1 FROM @cancelled);

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
