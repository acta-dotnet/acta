CREATE OR ALTER PROCEDURE {{schema}}.set_job_definition_overrides
    @p_id INT,
    @p_version INT,
    @p_priority_code_override TINYINT,
    @p_max_attempts_override SMALLINT,
    @p_backoff_override NVARCHAR(64),
    @p_execution_timeout_seconds_override INT,
    @p_deadline_seconds_override INT,
    @p_deadline_behavior_code_override TINYINT,
    @p_retention_seconds_override INT,
    @p_audit_level_code_override TINYINT,
    @p_alert_profile_code_override TINYINT,
    @p_alert_channel_name_override VARCHAR(128),
    @p_runbook_url_override VARCHAR(512),
    @p_display_name_override NVARCHAR(128),
    @p_description_override NVARCHAR(512),
    @p_actor_code TINYINT,
    @p_actor_key VARCHAR(128),
    @p_reason_code TINYINT,
    @p_reason_message NVARCHAR(512)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ns INT, @existing_version INT;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT
            @ns = jd.namespace_id,
            @existing_version = jd.version
        FROM {{schema}}.definitions jd
        WHERE jd.id = @p_id;

        IF @ns IS NULL
            BEGIN
                COMMIT TRANSACTION;
                SELECT CAST(2 /* DefinitionOverrideAction.NotFound */ AS TINYINT) AS action;
                RETURN;
            END;

        IF @existing_version <> @p_version
            BEGIN
                COMMIT TRANSACTION;
                SELECT CAST(3 /* DefinitionOverrideAction.VersionConflict */ AS TINYINT) AS action;
                RETURN;
            END;

        UPDATE {{schema}}.definitions SET
            priority_code_override = @p_priority_code_override,
            max_attempts_override = @p_max_attempts_override,
            backoff_override = @p_backoff_override,
            execution_timeout_seconds_override = @p_execution_timeout_seconds_override,
            deadline_seconds_override = @p_deadline_seconds_override,
            deadline_behavior_code_override = @p_deadline_behavior_code_override,
            retention_seconds_override = @p_retention_seconds_override,
            audit_level_code_override = @p_audit_level_code_override,
            alert_profile_code_override = @p_alert_profile_code_override,
            alert_channel_name_override = @p_alert_channel_name_override,
            runbook_url_override = @p_runbook_url_override,
            display_name_override = @p_display_name_override,
            description_override = @p_description_override,
            modified_at_utc = @now,
            version = jd.version + 1
        FROM {{schema}}.definitions jd
        WHERE jd.id = @p_id;

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
            30 /* EventCode.JobDefinitionOverridesUpdated */, @now, @ns,
            @p_actor_code, @p_actor_key,
            NULL, NULL, NULL,
            NULL, @p_id,
            NULL,
            NULL, NULL,
            NULL, NULL,
            @p_reason_code, @p_reason_message
        );

        COMMIT TRANSACTION;
        SELECT CAST(1 /* DefinitionOverrideAction.Applied */ AS TINYINT) AS action;
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
