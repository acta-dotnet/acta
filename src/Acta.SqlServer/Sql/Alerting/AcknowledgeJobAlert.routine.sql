CREATE OR ALTER PROCEDURE {{schema}}.acknowledge_job_alert
    @p_alert_ref UNIQUEIDENTIFIER,
    @p_actor_code TINYINT,
    @p_actor_key VARCHAR(128),
    @p_reason_message NVARCHAR(512)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(3) = SYSUTCDATETIME();
    DECLARE
        @namespace_id INT, @job_id BIGINT, @job_ref UNIQUEIDENTIFIER,
        @ack DATETIME2(7), @resolved DATETIME2(7),
        @definition_id INT, @lineage_root_id BIGINT, @execution_number INT;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT
            @namespace_id = a.namespace_id,
            @job_id = a.job_id,
            @job_ref = a.job_ref,
            @ack = a.acknowledged_at_utc,
            @resolved = a.resolved_at_utc
        FROM {{schema}}.alerts a WITH (UPDLOCK, ROWLOCK)
        WHERE a.alert_ref = @p_alert_ref;

        IF @namespace_id IS NULL
            BEGIN
                COMMIT TRANSACTION;
                SELECT
                    CAST(2 /* ControlAction.NotFound */ AS TINYINT) AS action,
                    CAST(NULL AS DATETIME2(7)) AS acknowledged_at_utc,
                    CAST(NULL AS DATETIME2(7)) AS resolved_at_utc;
                RETURN;
            END;

        IF @ack IS NOT NULL
            BEGIN
                COMMIT TRANSACTION;
                SELECT
                    CAST(1 /* ControlAction.Applied */ AS TINYINT) AS action,
                    @ack AS acknowledged_at_utc,
                    @resolved AS resolved_at_utc;
                RETURN;
            END;

        SELECT
            @definition_id = j.definition_id,
            @lineage_root_id = j.lineage_root_id
        FROM {{schema}}.jobs j
        WHERE j.id = @job_id;
        SELECT @execution_number = r.execution_number
        FROM {{schema}}.runtimes r
        WHERE r.job_id = @job_id;

        SET @ack = @now;

        UPDATE {{schema}}.alerts
        SET
            acknowledged_at_utc = @ack,
            modified_at_utc = @now,
            version = version + 1
        WHERE alert_ref = @p_alert_ref;

        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id,
            actor_code, actor_key,
            job_id, job_ref, execution_number,
            lineage_root_id, definition_id,
            worker_id,
            from_status_code, to_status_code,
            execution_status_code, duration_ms,
            reason_code, reason_message
        )
        VALUES (
            140 /* EventCode.AlertAcknowledged */, @now, @namespace_id,
            @p_actor_code, @p_actor_key,
            @job_id, @job_ref, @execution_number,
            COALESCE(@lineage_root_id, @job_id), @definition_id,
            NULL,
            NULL, NULL,
            NULL, NULL,
            NULL, @p_reason_message
        );

        COMMIT TRANSACTION;
        SELECT
            CAST(1 /* ControlAction.Applied */ AS TINYINT) AS action,
            @ack AS acknowledged_at_utc,
            @resolved AS resolved_at_utc;
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
