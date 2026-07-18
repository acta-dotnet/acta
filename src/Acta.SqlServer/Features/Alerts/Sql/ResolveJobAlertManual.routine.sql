CREATE OR ALTER PROCEDURE {{schema}}.resolve_job_alert_manual
    @p_id             BIGINT,
    @p_actor_code     TINYINT,
    @p_actor_key       VARCHAR(128),
    @p_reason_message NVARCHAR(512)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @namespace_id SMALLINT, @job_id BIGINT, @job_ref UNIQUEIDENTIFIER,
            @ack DATETIME2(7), @resolved DATETIME2(7),
            @definition_id INT, @lineage_root_id BIGINT, @execution_number INT;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @namespace_id = a.namespace_id, @job_id = a.job_id, @job_ref = a.job_ref,
               @ack = a.acknowledged_at_utc, @resolved = a.resolved_at_utc
          FROM {{schema}}.alerts a WITH (UPDLOCK, ROWLOCK)
         WHERE a.id = @p_id;

        IF @namespace_id IS NULL
        BEGIN
            COMMIT TRANSACTION;
            SELECT CAST(2 /* JobControlAction.NotFound */ AS TINYINT) AS action,
                   CAST(NULL AS DATETIME2(7)) AS acknowledged_at_utc, CAST(NULL AS DATETIME2(7)) AS resolved_at_utc;
            RETURN;
        END;

        IF @resolved IS NOT NULL
        BEGIN
            COMMIT TRANSACTION;
            SELECT CAST(1 /* JobControlAction.Applied */ AS TINYINT) AS action, @ack AS acknowledged_at_utc, @resolved AS resolved_at_utc;
            RETURN;
        END;

        SELECT @definition_id = j.definition_id, @lineage_root_id = j.lineage_root_id
          FROM {{schema}}.jobs j WHERE j.id = @job_id;
        SELECT @execution_number = r.execution_number
          FROM {{schema}}.runtimes r WHERE r.job_id = @job_id;

        SET @resolved = @now;

        UPDATE {{schema}}.alerts
           SET resolved_at_utc = @resolved,
               modified_at_utc = @now,
               version         = version + 1
         WHERE id = @p_id;

        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id,
            actor_code, actor_key,
            job_id, job_ref, execution_number,
            lineage_root_id, definition_id,
            worker_id,
            from_status_code, to_status_code,
            execution_status_code, duration_ms,
            reason_code, reason_message)
        VALUES (
            141 /* JobEventCode.AlertResolved */, @now, @namespace_id,
            @p_actor_code, @p_actor_key,
            @job_id, @job_ref, @execution_number,
            COALESCE(@lineage_root_id, @job_id), @definition_id,
            NULL,
            NULL, NULL,
            NULL, NULL,
            NULL, @p_reason_message);

        COMMIT TRANSACTION;
        SELECT CAST(1 /* JobControlAction.Applied */ AS TINYINT) AS action, @ack AS acknowledged_at_utc, @resolved AS resolved_at_utc;
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
