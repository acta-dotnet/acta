CREATE OR ALTER PROCEDURE {{schema}}.reset_job_state
    @p_id BIGINT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE
        @status TINYINT, @namespace_id INT,
        @lineage_root_id BIGINT, @definition_id INT, @tenant_id INT, @execution_number INT, @audit_level TINYINT,
        @job_ref UNIQUEIDENTIFIER;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT
            @status = r.status_code,
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

        IF @status IS NULL
            BEGIN
                COMMIT TRANSACTION;
                RETURN;
            END;

        DELETE FROM {{schema}}.checkpoints
        WHERE job_id = @p_id;
        DELETE FROM {{schema}}.steps
        WHERE job_id = @p_id;
        DELETE FROM {{schema}}.results
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
                    81 /* EventCode.JobStateReset */, @now, @namespace_id,
                    50 /* ActorCode.Job */, CONVERT(VARCHAR(128), @p_id),
                    @p_id, @job_ref, @execution_number,
                    COALESCE(@lineage_root_id, @p_id), @definition_id, @tenant_id,
                    NULL,
                    @status, @status,
                    NULL, NULL,
                    NULL, NULL
                );
            END

        COMMIT TRANSACTION;
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
