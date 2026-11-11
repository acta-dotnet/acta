SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE
        @from_status TINYINT, @namespace_id INT,
        @lineage_root_job_id BIGINT, @definition_id INT, @tenant_id INT, @execution_number INT,
        @version INT, @kind TINYINT;
    DECLARE @lane_id BIGINT, @to_status TINYINT = 10 /* JobStatusCode.Ready */, @fires BIT = 0;
    DECLARE @lock_result INT, @lock_resource NVARCHAR(255);

    /* Lock order: a slot's schedule lock, the lane, then the job's rows (docs/sql-internals.md, "Schedule lock" and
       "Lane lock order"): a slot's manual firing is an admission. Kind, namespace and lane_id never change, so the
       unlocked reads that find them are safe. */
    SELECT @lock_resource = N'{{schema}}.schedules.' + CAST(j.namespace_id AS NVARCHAR(12))
    FROM {{schema}}.jobs j
    WHERE j.job_id = @p_job_id AND j.job_kind_code = 20 /* JobKindCode.Permanent */;

    IF @lock_resource IS NOT NULL
        BEGIN
            EXEC @lock_result = sys.sp_getapplock @lock_resource, 'Shared', 'Transaction', -1;
            IF @lock_result < 0
                THROW 51205, 'ACTA:SCHEDULE_LOCK:The namespace schedule lock was not granted (deadlock victim or cancelled).', 1;
        END

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
        @version = r.version,
        @kind = j.job_kind_code
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

    -- Idle applies to a permanent slot alone.
    IF
        @from_status NOT IN (
            30 /* JobStatusCode.Paused */,
            20 /* JobStatusCode.Suspended */,
            10 /* JobStatusCode.Ready */,
            15 /* JobStatusCode.Blocked */
        )
        AND NOT (@from_status = 40 /* JobStatusCode.Idle */ AND @kind = 20 /* JobKindCode.Permanent */)
        BEGIN

            SELECT
                CAST(3 /* ControlAction.Rejected */ AS TINYINT) AS action,
                @from_status AS status,
                @version AS version;
            GOTO Finish;
        END;

    -- A retired definition's slot is inert: no manual firing until a build that carries the definition starts.
    IF
        @kind = 20 /* JobKindCode.Permanent */
        AND EXISTS (
            SELECT 1 FROM {{schema}}.definitions d
            WHERE d.definition_id = @definition_id AND d.definition_status_code = 240 /* DefinitionStatusCode.Retired */
        )
        BEGIN

            SELECT
                CAST(3 /* ControlAction.Rejected */ AS TINYINT) AS action,
                @from_status AS status,
                @version AS version;
            GOTO Finish;
        END;

    -- An Idle or Paused permanent slot with no firing outstanding takes a manual firing at the instant; a slot
    -- mid-firing moves its instant like any waiting job.
    IF
        @kind = 20 /* JobKindCode.Permanent */
        AND @from_status IN (40 /* JobStatusCode.Idle */, 30 /* JobStatusCode.Paused */)
        AND NOT EXISTS (
            SELECT 1
            FROM {{schema}}.checkpoints cp WITH (FORCESEEK)
            WHERE cp.job_id = @p_job_id AND cp.checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */
                AND cp.checkpoint_name = 'sys.firing' AND cp.checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */
        )
        SET @fires = 1;

    -- A suspended namespace takes no manual firing, read here after the lock a suspension holds exclusive; a slot
    -- mid-firing still moves its instant.
    IF
        @fires = 1
        AND NOT EXISTS (
            SELECT 1
            FROM {{schema}}.namespaces n
            WHERE n.namespace_id = @namespace_id AND n.namespace_status_code = 10 /* NamespaceStatusCode.Active */
        )
        BEGIN

            SELECT
                CAST(3 /* ControlAction.Rejected */ AS TINYINT) AS action,
                @from_status AS status,
                @version AS version;
            GOTO Finish;
        END;

    /* A laned job is rescheduled Ready unless another member runs, or it was not running and an older
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

    -- A null instant is now; a manual firing starts with a fresh failure budget.
    UPDATE {{schema}}.jobs_state
    SET
        next_run_at_utc = COALESCE(@p_next_run_at_utc, @now),
        job_status_code = @to_status,
        failure_count = CASE WHEN @fires = 1 THEN 0 ELSE failure_count END,
        modified_at_utc = @now,
        version = version + 1
    WHERE job_id = @p_job_id;
    SET @version = @version + 1;

    -- The manual envelope replaces the settled one of the slot's previous firing. The JSON is ASCII by construction,
    -- so the VARCHAR cast is its UTF-8.
    IF @fires = 1
        BEGIN
            UPDATE cp
            SET
                checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */,
                value_format_id = 1 /* JobPayloadFormat.Json */,
                value = CAST(CAST(@p_envelope AS VARCHAR(MAX)) AS VARBINARY(MAX)),
                modified_at_utc = @now,
                version = cp.version + 1
            FROM {{schema}}.checkpoints cp WITH (FORCESEEK)
            WHERE
                cp.job_id = @p_job_id
                AND cp.checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */
                AND cp.checkpoint_name = 'sys.firing';

            IF @@ROWCOUNT = 0
                INSERT INTO {{schema}}.checkpoints (
                    job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code,
                    value_format_id, value, created_at_utc, modified_at_utc, version)
                VALUES (
                    @p_job_id, 60 /* CheckpointKindCode.Firing */, 'sys.firing', 10 /* CheckpointStatusCode.Pending */,
                    1 /* JobPayloadFormat.Json */, CAST(CAST(@p_envelope AS VARCHAR(MAX)) AS VARBINARY(MAX)), @now, @now, 0);
        END;

    INSERT INTO {{schema}}.events (
        event_code, created_at_utc, namespace_id,
        entity_code, entity_id,
        definition_id, tenant_id, lineage_root_job_id,
        execution_number,
        event_message, details_format_id, details
    )
    VALUES (
        61 /* EventCode.JobRescheduled */, @now, @namespace_id,
        50 /* EntityCode.Job */, @p_job_id,
        @definition_id, @tenant_id, COALESCE(@lineage_root_job_id, @p_job_id),
        @execution_number,
        @p_event_message, @p_details_format_id, @p_details
    );

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
