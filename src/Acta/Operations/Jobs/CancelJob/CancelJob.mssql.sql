SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE
        @from_status TINYINT, @namespace_id INT,
        @lineage_root_job_id BIGINT, @definition_id INT, @tenant_id INT, @execution_number INT, @worker_id INT, @event_recording TINYINT,
        @parent_job_id BIGINT, @retention_seconds INT, @version INT, @lane_id BIGINT;

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
        @parent_job_id = j.parent_job_id,
        @worker_id = r.leased_by_worker_id,
        @version = r.version
    FROM {{schema}}.jobs_state r WITH (UPDLOCK, ROWLOCK)
    INNER JOIN {{schema}}.jobs j ON j.job_id = r.job_id
    WHERE r.job_id = @p_job_id;

    IF @from_status IS NULL
        BEGIN

            SELECT
                CAST(2 /* ControlAction.NotFound */ AS TINYINT) AS action,
                CAST(NULL AS TINYINT) AS status,
                CAST(NULL AS BIGINT) AS parent_job_id,
                CAST(NULL AS INT) AS version,
                CAST(0 AS TINYINT) AS lane_promoted;
            GOTO Finish;
        END;

    IF @p_expected_version IS NOT NULL AND @version <> @p_expected_version
        BEGIN

            SELECT
                CAST(5 /* ControlAction.VersionConflict */ AS TINYINT) AS action,
                @from_status AS status,
                @parent_job_id AS parent_job_id,
                @version AS version,
                CAST(0 AS TINYINT) AS lane_promoted;
            GOTO Finish;
        END;

    IF
        @from_status NOT IN (
            30 /* JobStatusCode.Paused */,
            20 /* JobStatusCode.Suspended */,
            10 /* JobStatusCode.Ready */,
            15 /* JobStatusCode.Blocked */,
            50 /* JobStatusCode.Executing */
        )
        BEGIN

            SELECT
                CAST(3 /* ControlAction.Rejected */ AS TINYINT) AS action,
                @from_status AS status,
                @parent_job_id AS parent_job_id,
                @version AS version,
                CAST(0 AS TINYINT) AS lane_promoted;
            GOTO Finish;
        END;

    SELECT @retention_seconds = jd.retention_seconds_effective
    FROM {{schema}}.definitions jd
    WHERE jd.definition_id = @definition_id;

    UPDATE {{schema}}.jobs_state
    SET
        job_status_code = 220 /* JobStatusCode.Cancelled */,
        leased_by_worker_id = NULL,
        lease_expires_at_utc = NULL,
        retention_until_utc = DATEADD(SECOND, @retention_seconds, @now),
        modified_at_utc = @now,
        version = version + 1
    WHERE job_id = @p_job_id;
    SET @version = @version + 1;

    /* A cancelled head hands its lane to the next member; a cancelled Blocked follower leaves the
       head where it is, which the promotion reads for itself. Promotion runs under the lane lock and
       re-reads rather than trusting an update that matched nothing. */
    DECLARE @head_id BIGINT, @head_status TINYINT, @promoted INT = 0;
    WHILE @lane_id IS NOT NULL AND @promoted = 0
        BEGIN
            SET @head_id = NULL;
            SET @head_status = NULL;

            SELECT TOP (1)
                @head_id = m.job_id,
                @head_status = m.job_status_code
            FROM {{schema}}.jobs_state m WITH (INDEX (ix_jobs_state_lane), FORCESEEK)
            WHERE
                m.lane_id = @lane_id
                AND m.lane_id IS NOT NULL
                AND m.job_status_code IN (
                    10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                    30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                )
            ORDER BY m.job_id;

            IF @head_status IS NULL OR @head_status <> 15 /* JobStatusCode.Blocked */
                BREAK;

            -- A restarted member can wait Blocked below a running one, so promote only while no member runs
            -- (docs/sql-execution-policy.md, "Lane lock order").
            IF EXISTS (
                SELECT 1
                FROM {{schema}}.jobs_state o WITH (INDEX (ix_jobs_state_lane), FORCESEEK)
                WHERE
                    o.lane_id = @lane_id
                    AND o.lane_id IS NOT NULL
                    AND o.job_status_code IN (
                        10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                        50 /* JobStatusCode.Executing */
                    )
            )
                BREAK;

            UPDATE {{schema}}.jobs_state
            SET
                job_status_code = 10 /* JobStatusCode.Ready */,
                next_run_at_utc = CASE WHEN next_run_at_utc > @now THEN next_run_at_utc ELSE @now END,
                modified_at_utc = @now,
                version = version + 1
            WHERE
                job_id = @head_id
                AND job_status_code = 15 /* JobStatusCode.Blocked */;

            SET @promoted = @@ROWCOUNT;
        END;

    IF @event_recording = 20 /* EventRecordingCode.Audit */
        BEGIN
            -- The attempt's details lead with its outcome, then the cancel's own: the writer's payload is a compact
            -- JSON object, so its members follow the outcome once its opening brace is dropped. Pure ASCII, so
            -- VARCHAR -> VARBINARY is UTF-8.
            IF @from_status = 50 /* JobStatusCode.Executing */
                BEGIN
                    INSERT INTO {{schema}}.events (
                        event_code, created_at_utc, namespace_id,
                        worker_id,
                        entity_code, entity_id,
                        definition_id, tenant_id, lineage_root_job_id,
                        execution_number,
                        event_message, details_format_id, details
                    )
                    VALUES (
                        43 /* EventCode.JobExecutionYielded */, @now, @namespace_id,
                        @worker_id,
                        50 /* EntityCode.Job */, @p_job_id,
                        @definition_id, @tenant_id, COALESCE(@lineage_root_job_id, @p_job_id),
                        @execution_number,
                        @p_event_message, 1 /* JobPayloadFormat.Json */,
                        CASE
                            WHEN @p_details IS NULL THEN CONVERT(VARBINARY(MAX), '{"outcome":"cancelled"}')
                            ELSE
                                CONVERT(VARBINARY(MAX), '{"outcome":"cancelled",')
                                + SUBSTRING(@p_details, 2, DATALENGTH(@p_details) - 1)
                        END
                    );
                END;

            INSERT INTO {{schema}}.events (
                event_code, created_at_utc, namespace_id,
                entity_code, entity_id,
                definition_id, tenant_id, lineage_root_job_id,
                execution_number,
                event_message, details_format_id, details
            )
            VALUES (
                70 /* EventCode.JobCancelled */, @now, @namespace_id,
                50 /* EntityCode.Job */, @p_job_id,
                @definition_id, @tenant_id, COALESCE(@lineage_root_job_id, @p_job_id),
                @execution_number,
                @p_event_message, @p_details_format_id, @p_details
            );
        END

    SELECT
        CAST(1 /* ControlAction.Applied */ AS TINYINT) AS action,
        CAST(220 /* JobStatusCode.Cancelled */ AS TINYINT) AS status,
        @parent_job_id AS parent_job_id,
        @version AS version,
        CAST(CASE WHEN @promoted > 0 THEN 1 ELSE 0 END AS TINYINT) AS lane_promoted;

Finish:

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
