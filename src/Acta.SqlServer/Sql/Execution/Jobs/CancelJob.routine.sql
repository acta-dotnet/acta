CREATE OR ALTER PROCEDURE {{schema}}.cancel_job
    @p_id BIGINT,
    @p_actor_code TINYINT,
    @p_actor_key NVARCHAR(128),
    @p_reason_code TINYINT,
    @p_reason_message NVARCHAR(512),
    @p_expected_version INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
        DECLARE
            @from_status TINYINT, @namespace_id INT,
            @lineage_root_id BIGINT, @definition_id INT, @tenant_id INT, @execution_number INT, @worker_id INT, @audit_level TINYINT,
            @parent_id BIGINT, @retention_seconds INT, @job_ref UNIQUEIDENTIFIER, @version INT, @lane_id BIGINT;

        /* Lock order: the lane, then the job's rows (docs/internals/sql-execution-policy.md, "Lane lock
           order"). lane_id never changes, so the unlocked read is safe. */
        SELECT @lane_id = r.lane_id
        FROM {{schema}}.runtimes r
        WHERE r.job_id = @p_id;

        IF @lane_id IS NOT NULL
            SELECT @lane_id = l.id
            FROM {{schema}}.lanes l WITH (UPDLOCK, ROWLOCK)
            WHERE l.id = @lane_id;

        SELECT
            @from_status = r.status_code,
            @namespace_id = j.namespace_id,
            @lineage_root_id = j.lineage_root_id,
            @definition_id = j.definition_id,
            @tenant_id = j.tenant_id,
            @execution_number = r.execution_number,
            @audit_level = j.audit_level_code,
            @parent_id = j.parent_id,
            @job_ref = j.job_ref,
            @worker_id = r.leased_by_worker_id,
            @version = r.version
        FROM {{schema}}.runtimes r WITH (UPDLOCK, ROWLOCK)
        INNER JOIN {{schema}}.jobs j ON j.id = r.job_id
        WHERE r.job_id = @p_id;

        IF @from_status IS NULL
            BEGIN

                SELECT
                    CAST(2 /* ControlAction.NotFound */ AS TINYINT) AS action,
                    CAST(NULL AS TINYINT) AS status_code,
                    CAST(NULL AS BIGINT) AS parent_id,
                    CAST(NULL AS INT) AS version,
                    CAST(0 AS TINYINT) AS lane_promoted;
                GOTO Finish;
            END;

        IF @p_expected_version IS NOT NULL AND @version <> @p_expected_version
            BEGIN

                SELECT
                    CAST(5 /* ControlAction.VersionConflict */ AS TINYINT) AS action,
                    @from_status AS status_code,
                    @parent_id AS parent_id,
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
                40 /* JobStatusCode.Dispatched */,
                50 /* JobStatusCode.Executing */
            )
            BEGIN

                SELECT
                    CAST(3 /* ControlAction.Rejected */ AS TINYINT) AS action,
                    @from_status AS status_code,
                    @parent_id AS parent_id,
                    @version AS version,
                    CAST(0 AS TINYINT) AS lane_promoted;
                GOTO Finish;
            END;

        SELECT @retention_seconds = jd.retention_seconds_effective
        FROM {{schema}}.definitions jd
        WHERE jd.id = @definition_id;

        UPDATE {{schema}}.runtimes
        SET
            status_code = 220 /* JobStatusCode.Cancelled */,
            leased_by_worker_id = NULL,
            lease_expires_at_utc = NULL,
            retention_until_utc = DATEADD(SECOND, @retention_seconds, @now),
            modified_at_utc = @now,
            version = version + 1
        WHERE job_id = @p_id;
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
                    @head_status = m.status_code
                FROM {{schema}}.runtimes m
                WHERE
                    m.lane_id = @lane_id
                    AND m.lane_id IS NOT NULL
                    AND m.status_code IN (
                        10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                        30 /* JobStatusCode.Paused */, 40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */
                    )
                ORDER BY m.job_id;

                IF @head_status IS NULL OR @head_status <> 15 /* JobStatusCode.Blocked */
                    BREAK;

                UPDATE {{schema}}.runtimes
                SET
                    status_code = 10 /* JobStatusCode.Ready */,
                    next_run_at_utc = CASE WHEN next_run_at_utc > @now THEN next_run_at_utc ELSE @now END,
                    modified_at_utc = @now,
                    version = version + 1
                WHERE
                    job_id = @head_id
                    AND status_code = 15 /* JobStatusCode.Blocked */;

                SET @promoted = @@ROWCOUNT;
            END;

        IF @audit_level = 20 /* JobAuditLevelCode.Audit */
            BEGIN
                IF @from_status = 50 /* JobStatusCode.Executing */
                    BEGIN
                        INSERT INTO {{schema}}.events (
                            event_code, created_at_utc, namespace_id,
                            actor_code, actor_key,
                            job_id, job_ref, execution_number,
                            lineage_root_id, definition_id, tenant_id,
                            worker_id,
                            from_status_code, to_status_code,
                            execution_status_code, duration_ms,
                            reason_code, reason_message
                        )
                        VALUES (
                            41 /* EventCode.JobExecutionFinished */, @now, @namespace_id,
                            @p_actor_code, @p_actor_key,
                            @p_id, @job_ref, @execution_number,
                            COALESCE(@lineage_root_id, @p_id), @definition_id, @tenant_id,
                            @worker_id,
                            50 /* JobStatusCode.Executing */, 220 /* JobStatusCode.Cancelled */,
                            220 /* ExecutionStatusCode.Cancelled */, NULL,
                            @p_reason_code, @p_reason_message
                        );
                    END;

                INSERT INTO {{schema}}.events (
                    event_code, created_at_utc, namespace_id,
                    actor_code, actor_key,
                    job_id, job_ref, execution_number,
                    lineage_root_id, definition_id, tenant_id,
                    worker_id,
                    from_status_code, to_status_code,
                    execution_status_code, duration_ms,
                    reason_code, reason_message
                )
                VALUES (
                    70 /* EventCode.JobCancelled */, @now, @namespace_id,
                    @p_actor_code, @p_actor_key,
                    @p_id, @job_ref, @execution_number,
                    COALESCE(@lineage_root_id, @p_id), @definition_id, @tenant_id,
                    NULL,
                    @from_status, 220 /* JobStatusCode.Cancelled */,
                    NULL, NULL,
                    @p_reason_code, @p_reason_message
                );
            END

        SELECT
            CAST(1 /* ControlAction.Applied */ AS TINYINT) AS action,
            CAST(220 /* JobStatusCode.Cancelled */ AS TINYINT) AS status_code,
            @parent_id AS parent_id,
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
END;
GO
