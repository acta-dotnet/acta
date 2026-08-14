CREATE OR ALTER PROCEDURE {{schema}}.pause_job
    @p_id BIGINT,
    @p_actor_code TINYINT,
    @p_actor_key VARCHAR(128),
    @p_reason_code TINYINT,
    @p_reason_message NVARCHAR(512)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE
        @from_status TINYINT, @namespace_id SMALLINT,
        @lineage_root_id BIGINT, @definition_id INT, @tenant_id INT, @execution_number INT, @audit_level TINYINT,
        @job_ref UNIQUEIDENTIFIER;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT
            @from_status = r.status_code,
            @namespace_id = j.namespace_id,
            @lineage_root_id = j.lineage_root_id,
            @definition_id = j.definition_id,
            @tenant_id = j.tenant_id,
            @execution_number = r.execution_number,
            @audit_level = j.audit_level_code,
            @job_ref = j.job_ref
        FROM {{schema}}.runtimes r WITH (UPDLOCK, ROWLOCK)
        INNER JOIN {{schema}}.jobs j ON j.id = r.job_id
        WHERE r.job_id = @p_id;

        IF @from_status IS NULL
            BEGIN
                COMMIT TRANSACTION;
                SELECT
                    CAST(2 /* JobControlAction.NotFound */ AS TINYINT) AS action,
                    CAST(NULL AS TINYINT) AS status_code;
                RETURN;
            END;

        IF
            @from_status NOT IN (
                30 /* JobStatusCode.Paused */,
                20 /* JobStatusCode.Suspended */,
                10 /* JobStatusCode.Ready */
            )
            BEGIN
                COMMIT TRANSACTION;
                SELECT
                    CAST(3 /* JobControlAction.Rejected */ AS TINYINT) AS action,
                    @from_status AS status_code;
                RETURN;
            END;

        UPDATE {{schema}}.runtimes
        SET
            status_code = 30 /* JobStatusCode.Paused */,
            modified_at_utc = @now,
            version = version + 1
        WHERE job_id = @p_id;

        IF @audit_level = 20 /* JobAuditLevelCode.Audit */
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
                    71 /* EventCode.JobPaused */, @now, @namespace_id,
                    @p_actor_code, @p_actor_key,
                    @p_id, @job_ref, @execution_number,
                    COALESCE(@lineage_root_id, @p_id), @definition_id, @tenant_id,
                    NULL,
                    @from_status, 30 /* JobStatusCode.Paused */,
                    NULL, NULL,
                    @p_reason_code, @p_reason_message
                );
            END

        COMMIT TRANSACTION;
        SELECT
            CAST(1 /* JobControlAction.Applied */ AS TINYINT) AS action,
            CAST(30 /* JobStatusCode.Paused */ AS TINYINT) AS status_code;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            BEGIN
                ROLLBACK TRANSACTION;
            END;

        THROW;
    END CATCH;
END;
GO
