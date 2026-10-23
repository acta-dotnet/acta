SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @now DATETIME2(3) = SYSUTCDATETIME();
    DECLARE
        @namespace_id INT, @alert_id BIGINT, @job_id BIGINT,
        @ack DATETIME2(7), @resolved DATETIME2(7),
        @definition_id INT, @tenant_id INT, @lineage_root_job_id BIGINT;

    SELECT
        @namespace_id = a.namespace_id,
        @alert_id = a.alert_id,
        @job_id = a.job_id,
        @ack = a.acknowledged_at_utc,
        @resolved = a.resolved_at_utc
    FROM {{schema}}.alerts a WITH (UPDLOCK, ROWLOCK)
    WHERE a.alert_ref = @p_alert_ref;

    IF @namespace_id IS NULL
        BEGIN

            SELECT
                CAST(2 /* ControlAction.NotFound */ AS TINYINT) AS action,
                CAST(NULL AS DATETIME2(7)) AS acknowledged_at_utc,
                CAST(NULL AS DATETIME2(7)) AS resolved_at_utc;
            GOTO Finish;
        END;

    IF @ack IS NOT NULL
        BEGIN

            SELECT
                CAST(1 /* ControlAction.Applied */ AS TINYINT) AS action,
                @ack AS acknowledged_at_utc,
                @resolved AS resolved_at_utc;
            GOTO Finish;
        END;

    SELECT
        @definition_id = j.definition_id,
        @tenant_id = j.tenant_id,
        @lineage_root_job_id = j.lineage_root_job_id
    FROM {{schema}}.jobs j
    WHERE j.job_id = @job_id;

    SET @ack = @now;

    UPDATE {{schema}}.alerts
    SET
        acknowledged_at_utc = @ack,
        modified_at_utc = @now,
        version = version + 1
    WHERE alert_ref = @p_alert_ref;

    INSERT INTO {{schema}}.events (
        event_code, created_at_utc, namespace_id,
        entity_code, entity_id,
        definition_id, tenant_id, lineage_root_job_id,
        event_message, details_format_id, details
    )
    VALUES (
        140 /* EventCode.AlertAcknowledged */, @now, @namespace_id,
        10 /* EntityCode.Alert */, @alert_id,
        @definition_id, @tenant_id, COALESCE(@lineage_root_job_id, @job_id),
        @p_event_message, @p_details_format_id, @p_details
    );

    SELECT
        CAST(1 /* ControlAction.Applied */ AS TINYINT) AS action,
        @ack AS acknowledged_at_utc,
        @resolved AS resolved_at_utc;

Finish:

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
