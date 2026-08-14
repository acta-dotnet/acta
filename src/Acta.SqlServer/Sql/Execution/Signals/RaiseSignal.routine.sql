CREATE OR ALTER PROCEDURE {{schema}}.raise_signal
    @p_job_id BIGINT,
    @p_kind_code TINYINT,
    @p_name VARCHAR(128),
    @p_value_format_id TINYINT,
    @p_value VARBINARY(MAX),
    @p_actor_code TINYINT,
    @p_actor_key VARCHAR(128),
    @p_reason_code TINYINT,
    @p_reason_message NVARCHAR(512)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @existing TINYINT;
    DECLARE
        @from_status TINYINT, @namespace_id SMALLINT, @lineage_root_id BIGINT,
        @definition_id INT, @tenant_id INT, @execution_number INT, @audit_level TINYINT,
        @job_ref UNIQUEIDENTIFIER;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @existing = status_code
        FROM {{schema}}.checkpoints WITH (UPDLOCK, HOLDLOCK)
        WHERE job_id = @p_job_id AND kind_code = @p_kind_code AND name = @p_name;

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
        WHERE r.job_id = @p_job_id;

        IF @from_status IS NULL
            BEGIN
                COMMIT TRANSACTION;
                SELECT
                    CAST(2 /* ControlAction.NotFound */ AS TINYINT) AS action,
                    CAST(NULL AS TINYINT) AS status_code;
                RETURN;
            END;

        IF @from_status IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
            BEGIN
                COMMIT TRANSACTION;
                SELECT
                    CAST(3 /* ControlAction.Rejected */ AS TINYINT) AS action,
                    @from_status AS status_code;
                RETURN;
            END;

        IF @existing IS NULL
            INSERT INTO {{schema}}.checkpoints (
                job_id, kind_code, name, status_code, value_format_id, value,
                created_at_utc, modified_at_utc, version
            )
            VALUES (@p_job_id, @p_kind_code, @p_name, 20 /* JobCheckpointStatusCode.Set */, @p_value_format_id, @p_value, @now, @now, 0);
        ELSE
            UPDATE {{schema}}.checkpoints
            SET
                status_code = 20 /* JobCheckpointStatusCode.Set */,
                value_format_id = @p_value_format_id,
                value = @p_value,
                modified_at_utc = @now,
                version = version + 1
            WHERE job_id = @p_job_id AND kind_code = @p_kind_code AND name = @p_name;

        IF @audit_level = 20 /* JobAuditLevelCode.Audit */
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
                80 /* EventCode.JobSignalRaised */, @now, @namespace_id,
                @p_actor_code, @p_actor_key,
                @p_job_id, @job_ref, @execution_number,
                COALESCE(@lineage_root_id, @p_job_id), @definition_id, @tenant_id,
                NULL,
                @from_status, @from_status,
                NULL, NULL,
                @p_reason_code, @p_reason_message
            );

        IF @from_status = 20 /* JobStatusCode.Suspended */
            BEGIN
                UPDATE {{schema}}.runtimes
                SET
                    status_code = 10 /* JobStatusCode.Ready */,
                    next_run_at_utc = @now,
                    modified_at_utc = @now,
                    version = version + 1
                WHERE job_id = @p_job_id;

                IF @audit_level = 20 /* JobAuditLevelCode.Audit */
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
                        72 /* EventCode.JobResumed */, @now, @namespace_id,
                        @p_actor_code, @p_actor_key,
                        @p_job_id, @job_ref, @execution_number,
                        COALESCE(@lineage_root_id, @p_job_id), @definition_id, @tenant_id,
                        NULL,
                        20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */,
                        NULL, NULL,
                        @p_reason_code, @p_reason_message
                    );

                COMMIT TRANSACTION;
                SELECT
                    CAST(1 /* ControlAction.Applied */ AS TINYINT) AS action,
                    CAST(10 /* JobStatusCode.Ready */ AS TINYINT) AS status_code;
                RETURN;
            END

        COMMIT TRANSACTION;
        SELECT
            CAST(1 /* ControlAction.Applied */ AS TINYINT) AS action,
            @from_status AS status_code;
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
