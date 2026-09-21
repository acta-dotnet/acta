CREATE OR ALTER PROCEDURE {{schema}}.register_job_definitions
    @p_namespace_id INT,
    @p_manifest_generation DATETIME2(7),
    @p_definitions {{schema}}.job_definition_batch READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        DECLARE @now DATETIME2(7) = SYSUTCDATETIME();

        UPDATE jd SET
            status_code = 10 /* JobDefinitionStatusCode.Active */,
            input_type_name = src.input_type_name,
            output_type_name = src.output_type_name,
            input_format_id = src.input_format_id,
            input_format_name = src.input_format_name,
            output_format_id = src.output_format_id,
            output_format_name = src.output_format_name,
            priority_code = src.priority_code,
            max_attempts = src.max_attempts,
            concurrency_limit = src.concurrency_limit,
            rate_limit = src.rate_limit,
            rate_key = src.rate_key,
            backoff = src.backoff,
            execution_timeout_seconds = src.execution_timeout_seconds,
            deadline_seconds = src.deadline_seconds,
            deadline_behavior_code = src.deadline_behavior_code,
            retention_seconds = src.retention_seconds,
            audit_level_code = src.audit_level_code,
            alert_profile_code = src.alert_profile_code,
            tenant_requirement_code = src.tenant_requirement_code,
            alert_channel_name = src.alert_channel_name,
            runbook_url = src.runbook_url,
            display_name = src.display_name,
            description = src.description,
            definition_hash = src.definition_hash,
            manifest_generation_at_utc = @p_manifest_generation,
            modified_at_utc = @now,
            version = jd.version + 1
        FROM {{schema}}.definitions jd
        INNER JOIN @p_definitions src
            ON jd.namespace_id = @p_namespace_id AND jd.name = src.name
        WHERE
            @p_manifest_generation >= jd.manifest_generation_at_utc
            AND (
                jd.status_code <> 10 /* JobDefinitionStatusCode.Active */
                OR jd.definition_hash <> src.definition_hash
            );

        INSERT INTO {{schema}}.definitions (
            namespace_id, name, status_code,
            input_type_name, output_type_name,
            input_format_id, input_format_name,
            output_format_id, output_format_name,
            priority_code, max_attempts, concurrency_limit,
            rate_limit, rate_key,
            backoff,
            execution_timeout_seconds,
            deadline_seconds,
            deadline_behavior_code,
            retention_seconds,
            audit_level_code, alert_profile_code,
            tenant_requirement_code,
            alert_channel_name, runbook_url,
            display_name, description,
            definition_hash, manifest_generation_at_utc,
            created_at_utc, modified_at_utc, version
        )
        SELECT
            @p_namespace_id,
            src.name,
            10 /* JobDefinitionStatusCode.Active */,
            src.input_type_name,
            src.output_type_name,
            src.input_format_id,
            src.input_format_name,
            src.output_format_id,
            src.output_format_name,
            src.priority_code,
            src.max_attempts,
            src.concurrency_limit,
            src.rate_limit,
            src.rate_key,
            src.backoff,
            src.execution_timeout_seconds,
            src.deadline_seconds,
            src.deadline_behavior_code,
            src.retention_seconds,
            src.audit_level_code,
            src.alert_profile_code,
            src.tenant_requirement_code,
            src.alert_channel_name,
            src.runbook_url,
            src.display_name,
            src.description,
            src.definition_hash,
            @p_manifest_generation,
            @now,
            @now,
            0
        FROM @p_definitions src
        WHERE NOT EXISTS (
            SELECT 1 FROM {{schema}}.definitions jd
            WHERE jd.namespace_id = @p_namespace_id AND jd.name = src.name
        );

        SELECT
            jd.name,
            jd.id
        FROM @p_definitions src
        INNER JOIN {{schema}}.definitions jd
            ON jd.namespace_id = @p_namespace_id AND jd.name = src.name;

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
