CREATE OR ALTER PROCEDURE {{schema}}.register_job_definitions
    @p_namespace_id    SMALLINT,
    @p_manifest_generation DATETIME2(7),
    @p_definitions         {{schema}}.job_definition_batch READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @retired TABLE (id INT PRIMARY KEY);

    BEGIN TRY
        BEGIN TRANSACTION;

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
            backoff = src.backoff,
            execution_timeout_seconds = src.execution_timeout_seconds,
            deadline_seconds = src.deadline_seconds,
            deadline_behavior_code = src.deadline_behavior_code,
            retention_seconds = src.retention_seconds,
            audit_level_code = src.audit_level_code,
            alert_profile_code = src.alert_profile_code,
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
         WHERE @p_manifest_generation >= jd.manifest_generation_at_utc
           AND (
                jd.status_code <> 10 /* JobDefinitionStatusCode.Active */
             OR jd.definition_hash <> src.definition_hash
           );

        INSERT INTO {{schema}}.definitions (
            namespace_id, name, status_code,
            input_type_name, output_type_name,
            input_format_id, input_format_name,
            output_format_id, output_format_name,
            priority_code, max_attempts,
            backoff,
            execution_timeout_seconds,
            deadline_seconds,
            deadline_behavior_code,
            retention_seconds,
            audit_level_code, alert_profile_code,
            alert_channel_name, runbook_url,
            display_name, description,
            definition_hash, manifest_generation_at_utc,
            created_at_utc, modified_at_utc, version)
        SELECT
            @p_namespace_id, src.name, 10 /* JobDefinitionStatusCode.Active */,
            src.input_type_name, src.output_type_name,
            src.input_format_id, src.input_format_name,
            src.output_format_id, src.output_format_name,
            src.priority_code, src.max_attempts,
            src.backoff,
            src.execution_timeout_seconds,
            src.deadline_seconds,
            src.deadline_behavior_code,
            src.retention_seconds,
            src.audit_level_code, src.alert_profile_code,
            src.alert_channel_name, src.runbook_url,
            src.display_name, src.description,
            src.definition_hash, @p_manifest_generation,
            @now, @now, 0
          FROM @p_definitions src
         WHERE NOT EXISTS (
             SELECT 1 FROM {{schema}}.definitions jd
              WHERE jd.namespace_id = @p_namespace_id AND jd.name = src.name);

        -- Retire definitions absent from the manifest and capture the ids this call actually flipped,
        -- so the cancel-sweep can be scoped to exactly that set.
        UPDATE jd SET
            status_code = 240 /* JobDefinitionStatusCode.Retired */,
            modified_at_utc = @now,
            version = jd.version + 1
          OUTPUT inserted.id INTO @retired
          FROM {{schema}}.definitions jd WITH (INDEX(ux_definitions_namespace_name))
         WHERE jd.namespace_id = @p_namespace_id
           AND jd.status_code = 10 /* JobDefinitionStatusCode.Active */
           AND jd.manifest_generation_at_utc <= @p_manifest_generation
           AND NOT EXISTS (SELECT 1 FROM @p_definitions src WHERE src.name = jd.name);

        -- Retirement cancel-sweep: parked rows of definitions this call transitioned to retired.
        -- Definitions retired by an earlier call keep their parked jobs (a re-arm after retirement stays
        -- as the operator left it). In-flight Dispatched/Executing rows finish their attempt untouched.
        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id,
            actor_code, actor_key,
            job_id, job_ref, execution_number,
            lineage_root_id, definition_id, tenant_id,
            worker_id,
            from_status_code, to_status_code,
            execution_status_code, duration_ms,
            reason_code, reason_message)
        SELECT
            70 /* JobEventCode.JobCancelled */, @now, j.namespace_id,
            10 /* JobActorCode.Sys */, 'sys:register-definitions',
            j.id, j.job_ref, r.execution_number,
            COALESCE(j.lineage_root_id, j.id), j.definition_id, j.tenant_id,
            NULL,
            r.status_code, 220 /* JobStatusCode.Cancelled */,
            NULL, NULL,
            42 /* JobEventReasonCode.JobDefinitionRetired */, NULL
          FROM {{schema}}.jobs j
          INNER JOIN {{schema}}.runtimes r ON r.job_id = j.id
          INNER JOIN {{schema}}.definitions jd ON jd.id = j.definition_id
         WHERE jd.namespace_id = @p_namespace_id
           AND jd.id IN (SELECT id FROM @retired)
           AND j.audit_level_code = 20 /* JobAuditLevelCode.Audit */
           AND r.status_code IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */, 30 /* JobStatusCode.Paused */);

        UPDATE r SET
            status_code          = 220 /* JobStatusCode.Cancelled */,
            leased_by_worker_id  = NULL,
            lease_expires_at_utc = NULL,
            retention_until_utc  = DATEADD(SECOND, jd.retention_seconds_effective, @now),
            modified_at_utc      = @now,
            version              = r.version + 1
          FROM {{schema}}.runtimes r
          INNER JOIN {{schema}}.jobs j ON j.id = r.job_id
          INNER JOIN {{schema}}.definitions jd ON jd.id = j.definition_id
         WHERE jd.namespace_id = @p_namespace_id
           AND jd.id IN (SELECT id FROM @retired)
           AND r.status_code IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */, 30 /* JobStatusCode.Paused */);

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
        BEGIN
            ROLLBACK TRANSACTION;
        END;

        THROW;
    END CATCH;

    SELECT jd.name, jd.id
      FROM @p_definitions src
      INNER JOIN {{schema}}.definitions jd
              ON jd.namespace_id = @p_namespace_id AND jd.name = src.name;
END;
GO
