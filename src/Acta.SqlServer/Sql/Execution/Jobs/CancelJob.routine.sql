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
            @parent_id BIGINT, @retention_seconds INT, @job_ref UNIQUEIDENTIFIER, @version INT;

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
                    CAST(NULL AS INT) AS version;
                GOTO Finish;
            END;

        IF @p_expected_version IS NOT NULL AND @version <> @p_expected_version
            BEGIN

                SELECT
                    CAST(5 /* ControlAction.VersionConflict */ AS TINYINT) AS action,
                    @from_status AS status_code,
                    @parent_id AS parent_id,
                    @version AS version;
                GOTO Finish;
            END;

        IF
            @from_status NOT IN (
                30 /* JobStatusCode.Paused */,
                20 /* JobStatusCode.Suspended */,
                10 /* JobStatusCode.Ready */,
                40 /* JobStatusCode.Dispatched */,
                50 /* JobStatusCode.Executing */
            )
            BEGIN

                SELECT
                    CAST(3 /* ControlAction.Rejected */ AS TINYINT) AS action,
                    @from_status AS status_code,
                    @parent_id AS parent_id,
                    @version AS version;
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
END;
GO
