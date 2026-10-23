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
        @version INT;
    DECLARE @lane_id BIGINT, @to_status TINYINT = 10 /* JobStatusCode.Ready */;

    /* Lock order: the lane, then the job's rows (docs/sql-execution-policy.md, "Lane lock
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
        @version = r.version
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

    IF @from_status <> 30 /* JobStatusCode.Paused */
        BEGIN

            SELECT
                CAST(3 /* ControlAction.Rejected */ AS TINYINT) AS action,
                @from_status AS status,
                @version AS version;
            GOTO Finish;
        END;

    /* A laned job resumes Ready unless another member runs or an older member is unfinished
       (docs/sql-execution-policy.md, "Lane lock order"). */
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
                    OR o.job_id < @p_job_id
                )
        )
        SET @to_status = 15 /* JobStatusCode.Blocked */;

    UPDATE {{schema}}.jobs_state
    SET
        job_status_code = @to_status,
        next_run_at_utc = COALESCE(@p_next_run_at_utc, @now),
        modified_at_utc = @now,
        version = version + 1
    WHERE job_id = @p_job_id;
    SET @version = @version + 1;

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
                72 /* EventCode.JobResumed */, @now, @namespace_id,
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
