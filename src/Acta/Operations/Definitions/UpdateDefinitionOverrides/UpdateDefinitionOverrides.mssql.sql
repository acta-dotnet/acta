SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ns INT, @existing_version INT, @rate_name VARCHAR(128), @name VARCHAR(128), @meter VARCHAR(128);
    DECLARE @lock_result INT, @lock_resource NVARCHAR(255);

    -- The namespace's schedule lock, exclusive, before any row lock (docs/sql-internals.md, "Schedule lock"): the
    -- definition row and then its slot lock below, and registration writes the slot before its schedules check the row.
    SELECT @lock_resource = N'{{schema}}.schedules.' + CAST(jd.namespace_id AS NVARCHAR(12))
    FROM {{schema}}.definitions jd
    WHERE jd.definition_id = @p_definition_id;

    IF @lock_resource IS NOT NULL
        BEGIN
            EXEC @lock_result = sys.sp_getapplock @lock_resource, 'Exclusive', 'Transaction', -1;
            IF @lock_result < 0
                THROW 51205, 'ACTA:SCHEDULE_LOCK:The namespace schedule lock was not granted (deadlock victim or cancelled).', 1;
        END;

    SELECT
        @ns = jd.namespace_id,
        @existing_version = jd.version,
        @rate_name = jd.rate_name,
        @name = jd.definition_name
    FROM {{schema}}.definitions jd WITH (UPDLOCK, ROWLOCK)
    WHERE jd.definition_id = @p_definition_id;

    IF @ns IS NULL
        BEGIN
            SELECT CAST(2 /* DefinitionOverrideAction.NotFound */ AS TINYINT) AS action, CAST(NULL AS INT) AS version;
            GOTO Finish;
        END;

    IF @existing_version <> @p_version
        BEGIN
            SELECT CAST(3 /* DefinitionOverrideAction.VersionConflict */ AS TINYINT) AS action, @existing_version AS version;
            GOTO Finish;
        END;

    SET @meter = LOWER(COALESCE(@rate_name, @name));

    UPDATE {{schema}}.definitions SET
        job_priority_code_override = @p_job_priority_code_override,
        max_attempts_override = @p_max_attempts_override,
        concurrency_limit_override = @p_concurrency_limit_override,
        rate_limit_override = @p_rate_limit_override,
        backoff_override = @p_backoff_override,
        execution_timeout_seconds_override = @p_execution_timeout_seconds_override,
        deadline_seconds_override = @p_deadline_seconds_override,
        deadline_behavior_code_override = @p_deadline_behavior_code_override,
        retention_seconds_override = @p_retention_seconds_override,
        event_recording_code_override = @p_event_recording_code_override,
        alert_profile_code_override = @p_alert_profile_code_override,
        alert_channel_name_override = @p_alert_channel_name_override,
        runbook_url_override = @p_runbook_url_override,
        display_name_override = @p_display_name_override,
        description_override = @p_description_override,
        modified_at_utc = @now,
        version = jd.version + 1
    FROM {{schema}}.definitions jd
    WHERE jd.definition_id = @p_definition_id AND jd.version = @p_version;

    -- A definition on a meter carries every sibling that already has a rate (declared or overridden)
    -- on the same effective key along with it here, so the meter never carries two rates and the
    -- last write wins for every participant. See JobDefinitionPolicyOverrides.RateLimit.
    UPDATE {{schema}}.definitions
    SET
        rate_limit_override = @p_rate_limit_override,
        modified_at_utc = @now,
        version = version + 1
    WHERE
        namespace_id = @ns
        AND definition_id <> @p_definition_id
        AND (rate_limit IS NOT NULL OR rate_limit_override IS NOT NULL)
        AND LOWER(COALESCE(rate_name, definition_name)) = @meter;

    -- A recurring job's slot is one long-lived job, so it takes the new event recording here, at once, rather than at
    -- the next deploy's registration; an ordinary job keeps the recording it was enqueued with.
    UPDATE j
    SET event_recording_code = jd.event_recording_code_effective
    FROM {{schema}}.jobs j
    INNER JOIN {{schema}}.definitions jd ON jd.definition_id = j.definition_id
    WHERE
        j.definition_id = @p_definition_id
        AND j.job_kind_code = 20 /* JobKindCode.PermanentSlot */
        AND j.event_recording_code <> jd.event_recording_code_effective;

    INSERT INTO {{schema}}.events (
        event_code, created_at_utc, namespace_id,
        entity_code, entity_id,
        definition_id,
        event_message, details_format_id, details
    )
    VALUES (
        30 /* EventCode.JobDefinitionOverridesUpdated */, @now, @ns,
        30 /* EntityCode.Definition */, @p_definition_id,
        @p_definition_id,
        @p_event_message, @p_details_format_id, @p_details
    );

    SELECT CAST(1 /* DefinitionOverrideAction.Applied */ AS TINYINT) AS action, @existing_version + 1 AS version;

Finish:

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
