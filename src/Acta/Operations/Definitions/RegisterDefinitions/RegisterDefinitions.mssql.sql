SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();

    UPDATE jd SET
        definition_status_code = 10 /* DefinitionStatusCode.Active */,
        input_type_name = src.input_type_name,
        output_type_name = src.output_type_name,
        input_format_id = src.input_format_id,
        input_format_name = src.input_format_name,
        output_format_id = src.output_format_id,
        output_format_name = src.output_format_name,
        job_priority_code = src.job_priority_code,
        max_attempts = src.max_attempts,
        concurrency_limit = src.concurrency_limit,
        rate_limit = src.rate_limit,
        rate_name = src.rate_name,
        lane_name = src.lane_name,
        backoff = src.backoff,
        execution_timeout_seconds = src.execution_timeout_seconds,
        deadline_seconds = src.deadline_seconds,
        deadline_behavior_code = src.deadline_behavior_code,
        retention_seconds = src.retention_seconds,
        event_recording_code = src.event_recording_code,
        alert_profile_code = src.alert_profile_code,
        tenant_requirement_code = src.tenant_requirement_code,
        alert_channel_name = src.alert_channel_name,
        runbook_url = src.runbook_url,
        display_name = src.display_name,
        description = src.description,
        definition_hash = src.definition_hash,
        manifest_generation_at_utc = @p_manifest_generation,
        -- An unchanged Active row from a newer manifest takes only the generation, because an equal hash means the columns
        -- above already hold these values; version and modified_at_utc stay, so no operator CAS fails and no worker reloads.
        modified_at_utc = CASE WHEN jd.definition_status_code <> 10 /* DefinitionStatusCode.Active */
            OR jd.definition_hash <> src.definition_hash
            THEN @now ELSE jd.modified_at_utc END,
        version = CASE WHEN jd.definition_status_code <> 10 /* DefinitionStatusCode.Active */
            OR jd.definition_hash <> src.definition_hash
            THEN jd.version + 1 ELSE jd.version END
    FROM {{schema}}.definitions jd
    INNER JOIN @p_definitions src
        ON jd.namespace_id = @p_namespace_id AND jd.definition_name = src.definition_name
    WHERE
        @p_manifest_generation >= jd.manifest_generation_at_utc
        AND (
            jd.definition_status_code <> 10 /* DefinitionStatusCode.Active */
            OR jd.definition_hash <> src.definition_hash
            -- An unchanged row still takes a newer generation, so an older build that registers after it is refused.
            OR @p_manifest_generation > jd.manifest_generation_at_utc
        );

    INSERT INTO {{schema}}.definitions (
        namespace_id, definition_name, definition_status_code,
        input_type_name, output_type_name,
        input_format_id, input_format_name,
        output_format_id, output_format_name,
        job_priority_code, max_attempts, concurrency_limit,
        rate_limit, rate_name, lane_name,
        backoff,
        execution_timeout_seconds,
        deadline_seconds,
        deadline_behavior_code,
        retention_seconds,
        event_recording_code, alert_profile_code,
        tenant_requirement_code,
        alert_channel_name, runbook_url,
        display_name, description,
        definition_hash, manifest_generation_at_utc,
        created_at_utc, modified_at_utc, version
    )
    SELECT
        @p_namespace_id,
        src.definition_name,
        10 /* DefinitionStatusCode.Active */,
        src.input_type_name,
        src.output_type_name,
        src.input_format_id,
        src.input_format_name,
        src.output_format_id,
        src.output_format_name,
        src.job_priority_code,
        src.max_attempts,
        src.concurrency_limit,
        src.rate_limit,
        src.rate_name,
        src.lane_name,
        src.backoff,
        src.execution_timeout_seconds,
        src.deadline_seconds,
        src.deadline_behavior_code,
        src.retention_seconds,
        src.event_recording_code,
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
        WHERE jd.namespace_id = @p_namespace_id AND jd.definition_name = src.definition_name
    );

    SELECT
        jd.definition_name AS def_name,
        jd.definition_id AS def_id
    FROM @p_definitions src
    INNER JOIN {{schema}}.definitions jd
        ON jd.namespace_id = @p_namespace_id AND jd.definition_name = src.definition_name;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
