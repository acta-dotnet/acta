SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE
        @from_status TINYINT, @namespace_id INT,
        @lineage_root_job_id BIGINT, @definition_id INT, @tenant_id INT, @execution_number INT, @event_recording TINYINT,
        @version INT, @parent_job_id BIGINT, @schedule_id INT;
    DECLARE @lane_id BIGINT, @to_status TINYINT = 10 /* JobStatusCode.Ready */;

    /* Lock order: the lane, then the job's rows (docs/sql-internals.md, "Lane lock
       order"). lane_id never changes, so the unlocked read is safe. */
    SELECT @lane_id = r.lane_id
    FROM {{schema}}.jobs_state r
    WHERE r.job_id = @p_job_id;

    IF @lane_id IS NOT NULL
        SELECT @lane_id = l.lane_id
        FROM {{schema}}.lanes l WITH (UPDLOCK, ROWLOCK)
        WHERE l.lane_id = @lane_id;

    SELECT
        @from_status = r.job_status_code,
        @namespace_id = j.namespace_id,
        @lineage_root_job_id = j.lineage_root_job_id,
        @definition_id = j.definition_id,
        @tenant_id = j.tenant_id,
        @execution_number = r.execution_number,
        @event_recording = j.event_recording_code,
        @version = r.version,
        @parent_job_id = j.parent_job_id,
        @schedule_id = j.schedule_id
    FROM {{schema}}.jobs_state r WITH (UPDLOCK, ROWLOCK)
    INNER JOIN {{schema}}.jobs j ON j.job_id = r.job_id
    WHERE r.job_id = @p_job_id;

    IF @from_status IS NULL
        BEGIN

            SELECT
                CAST(2 /* ControlAction.NotFound */ AS TINYINT) AS action,
                CAST(NULL AS TINYINT) AS status,
                CAST(NULL AS INT) AS version;
            GOTO Finish;
        END;

    IF @p_expected_version IS NOT NULL AND @version <> @p_expected_version
        BEGIN

            SELECT
                CAST(5 /* ControlAction.VersionConflict */ AS TINYINT) AS action,
                @from_status AS status,
                @version AS version;
            GOTO Finish;
        END;

    IF @from_status = 50 /* JobStatusCode.Executing */
        BEGIN

            SELECT
                CAST(3 /* ControlAction.Rejected */ AS TINYINT) AS action,
                @from_status AS status,
                @version AS version;
            GOTO Finish;
        END;

    DECLARE @slot BIT = CASE WHEN EXISTS (
        SELECT 1 FROM {{schema}}.jobs j WHERE j.job_id = @p_job_id AND j.job_kind_code = 20 /* JobKindCode.PermanentSlot */
    ) THEN 1 ELSE 0 END;

    -- A slot whose firing is still outstanding is not restarted around it; one with none restarts Idle, for admission.
    IF @slot = 1 AND EXISTS (
        SELECT 1
        FROM {{schema}}.checkpoints cp
        WHERE cp.job_id = @p_job_id AND cp.checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */
            AND cp.checkpoint_name = 'sys.firing' AND cp.checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */
    )
        BEGIN

            SELECT
                CAST(3 /* ControlAction.Rejected */ AS TINYINT) AS action,
                @from_status AS status,
                @version AS version;
            GOTO Finish;
        END;

    IF @slot = 1
        SET @to_status = 40 /* JobStatusCode.Idle */;

    /* A finished laned job is not reactivated while an unfinished ancestor or descendant sits in its
       lane, the state the enqueue ancestor guard refuses (docs/sql-internals.md, "Lane
       lock order"). Both walks run under the lane lock, which every enqueue into the lane takes too. */
    IF
        @lane_id IS NOT NULL
        AND @from_status IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
        BEGIN
            DECLARE @lineage_hit BIT = 0;

            WITH ancestors AS (
                SELECT a.job_id, a.parent_job_id
                FROM {{schema}}.jobs a
                WHERE a.job_id = @parent_job_id
                UNION ALL
                SELECT a.job_id, a.parent_job_id
                FROM {{schema}}.jobs a
                INNER JOIN ancestors c ON a.job_id = c.parent_job_id
            )

            SELECT TOP (1) @lineage_hit = 1
            FROM ancestors c
            INNER JOIN {{schema}}.jobs_state lr ON lr.job_id = c.job_id
            WHERE
                lr.lane_id = @lane_id
                AND lr.job_status_code IN (
                    10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                    30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                )
            OPTION (MAXRECURSION 0);

            WITH descendants AS (
                SELECT d.job_id
                FROM {{schema}}.jobs d
                WHERE
                    d.parent_job_id = @p_job_id
                    AND d.parent_job_id IS NOT NULL
                UNION ALL
                SELECT d.job_id
                FROM {{schema}}.jobs d
                INNER JOIN descendants c ON d.parent_job_id = c.job_id
                WHERE d.parent_job_id IS NOT NULL
            )

            SELECT TOP (1) @lineage_hit = 1
            FROM descendants c
            INNER JOIN {{schema}}.jobs_state lr ON lr.job_id = c.job_id
            WHERE
                lr.lane_id = @lane_id
                AND lr.job_status_code IN (
                    10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                    30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                )
            OPTION (MAXRECURSION 0);

            IF @lineage_hit = 1
                BEGIN

                    SELECT
                        CAST(3 /* ControlAction.Rejected */ AS TINYINT) AS action,
                        @from_status AS status,
                        @version AS version;
                    GOTO Finish;
                END;
        END;

    /* A laned job restarts Ready unless another member runs, or it was not running and an older
       member is unfinished (docs/sql-internals.md, "Lane lock order"). */
    IF
        @lane_id IS NOT NULL
        AND EXISTS (
            SELECT 1
            FROM {{schema}}.jobs_state o WITH (INDEX (ix_jobs_state_lane), FORCESEEK)
            WHERE
                o.lane_id = @lane_id
                AND o.lane_id IS NOT NULL
                AND o.job_status_code IN (
                    10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                    30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                )
                AND o.job_id <> @p_job_id
                AND (
                    o.job_status_code IN (
                        10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                        50 /* JobStatusCode.Executing */
                    )
                    OR (
                        @from_status NOT IN (
                            10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */
                        )
                        AND o.job_id < @p_job_id
                    )
                )
        )
        SET @to_status = 15 /* JobStatusCode.Blocked */;

    -- A finished charged root runs again as a new firing of its schedule, charged to it again, only while the schedule
    -- is live and below MaxOutstanding, the check and the charge one write under the state row's lock; otherwise the
    -- restart is Rejected. Its settled envelope is then outstanding again.
    IF @schedule_id IS NOT NULL AND @from_status IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
        BEGIN
            UPDATE st
            SET
                outstanding_count = st.outstanding_count + 1,
                modified_at_utc = @now,
                version = st.version + 1
            FROM {{schema}}.schedules_state st WITH (UPDLOCK, ROWLOCK, FORCESEEK)
            INNER JOIN {{schema}}.schedules js ON js.schedule_id = st.schedule_id
            INNER JOIN {{schema}}.definitions d ON d.definition_id = js.definition_id
            WHERE
                st.schedule_id = @schedule_id
                AND js.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
                AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
                AND st.outstanding_count < js.max_outstanding;
            IF @@ROWCOUNT = 0
                BEGIN
                    SELECT
                        CAST(3 /* ControlAction.Rejected */ AS TINYINT) AS action,
                        @from_status AS status,
                        @version AS version;
                    GOTO Finish;
                END;

            UPDATE cp
            SET
                checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */,
                modified_at_utc = @now,
                version = cp.version + 1
            FROM {{schema}}.checkpoints cp WITH (FORCESEEK)
            WHERE
                cp.job_id = @p_job_id
                AND cp.checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */
                AND cp.checkpoint_name = 'sys.firing';
        END;

    UPDATE {{schema}}.jobs_state
    SET
        job_status_code = @to_status,
        failure_count = 0,
        next_run_at_utc = CASE WHEN @to_status = 40 /* JobStatusCode.Idle */ THEN NULL ELSE COALESCE(@p_next_run_at_utc, @now) END,
        leased_by_worker_id = NULL,
        lease_expires_at_utc = NULL,
        retention_until_utc = NULL,
        result_format_id = 0 /* JobPayloadFormat.None */,
        result = NULL,
        modified_at_utc = @now,
        version = version + 1
    WHERE job_id = @p_job_id;
    SET @version = @version + 1;

    -- The members parked on the slot evaluate again now that it is Idle.
    IF @to_status = 40 /* JobStatusCode.Idle */
        UPDATE st
        SET
            next_evaluation_at_utc = @now,
            evaluation_status_code = 10 /* EvaluationStatusCode.Scheduled */,
            modified_at_utc = @now,
            version = st.version + 1
        FROM {{schema}}.schedules_state st WITH (FORCESEEK)
        INNER JOIN {{schema}}.schedules js ON js.schedule_id = st.schedule_id
        WHERE
            js.job_id = @p_job_id
            AND st.evaluation_status_code IN (20 /* EvaluationStatusCode.WaitingForCapacity */, 50 /* EvaluationStatusCode.SlotUnavailable */);

    -- An exhausted step runs again from a fresh budget and retry window; an interrupted at-most-once
    -- step stays, since only someone who reconciled its outcome can say it may run again.
    DELETE FROM {{schema}}.steps
    WHERE
        job_id = @p_job_id
        AND step_status_code = 200 /* StepStatusCode.Exhausted */;

    IF @event_recording = 20 /* EventRecordingCode.Audit */
        BEGIN
            INSERT INTO {{schema}}.events (
                event_code, created_at_utc, namespace_id,
                entity_code, entity_id,
                definition_id, tenant_id, lineage_root_job_id,
                execution_number,
                event_message, details_format_id, details
            )
            VALUES (
                73 /* EventCode.JobRestarted */, @now, @namespace_id,
                50 /* EntityCode.Job */, @p_job_id,
                @definition_id, @tenant_id, COALESCE(@lineage_root_job_id, @p_job_id),
                @execution_number,
                @p_event_message, @p_details_format_id, @p_details
            );
        END

    SELECT
        CAST(1 /* ControlAction.Applied */ AS TINYINT) AS action,
        @to_status AS status,
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
