SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
DECLARE @ns INT, @existing_version INT, @existing_status TINYINT, @retention_seconds INT, @action TINYINT;
DECLARE @parked TABLE (
    job_id BIGINT NOT NULL PRIMARY KEY,
    job_ref UNIQUEIDENTIFIER NOT NULL,
    parent_id BIGINT NULL,
    namespace_id INT NOT NULL,
    tenant_id INT NULL,
    definition_id INT NOT NULL,
    lineage_root_id BIGINT NOT NULL,
    audit_level_code TINYINT NOT NULL);
DECLARE @cancelled TABLE (
    job_id BIGINT NOT NULL PRIMARY KEY,
    from_status_code TINYINT NOT NULL,
    execution_number INT NOT NULL);

BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    SELECT
        @ns = jd.namespace_id,
        @existing_version = jd.version,
        @existing_status = jd.status_code,
        @retention_seconds = jd.retention_seconds_effective
    FROM {{schema}}.definitions jd WITH (UPDLOCK, ROWLOCK)
    WHERE jd.id = @p_id;

    SET @action = CASE
        WHEN @ns IS NULL THEN 2 /* DefinitionOverrideAction.NotFound */
        WHEN @existing_version <> @p_version THEN 3 /* DefinitionOverrideAction.VersionConflict */
        ELSE 1 /* DefinitionOverrideAction.Applied */
    END;

    IF @action = 1 /* DefinitionOverrideAction.Applied */ AND @existing_status <> 240 /* JobDefinitionStatusCode.Retired */
    BEGIN
        UPDATE {{schema}}.definitions
        SET
            status_code = 240 /* JobDefinitionStatusCode.Retired */,
            modified_at_utc = @now,
            version = version + 1
        WHERE id = @p_id;

        /* The parked set is materialized first so the runtimes update drives off a table variable and
           can seek pk_runtimes, per the SQL Server join-update rule in sql-execution-policy.md. */
        INSERT INTO @parked (
            job_id,
            job_ref,
            parent_id,
            namespace_id,
            tenant_id,
            definition_id,
            lineage_root_id,
            audit_level_code)
        SELECT
            j.id,
            j.job_ref,
            j.parent_id,
            j.namespace_id,
            j.tenant_id,
            j.definition_id,
            COALESCE(j.lineage_root_id, j.id),
            j.audit_level_code
        FROM {{schema}}.jobs j
        INNER JOIN {{schema}}.runtimes r ON r.job_id = j.id
        WHERE
            j.definition_id = @p_id
            AND r.status_code IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */, 30 /* JobStatusCode.Paused */);

        UPDATE r
        SET
            status_code = 220 /* JobStatusCode.Cancelled */,
            leased_by_worker_id = NULL,
            lease_expires_at_utc = NULL,
            retention_until_utc = DATEADD(SECOND, @retention_seconds, @now),
            modified_at_utc = @now,
            version = r.version + 1
        OUTPUT INSERTED.job_id, DELETED.status_code, INSERTED.execution_number
            INTO @cancelled (job_id, from_status_code, execution_number)
        FROM {{schema}}.runtimes r WITH (FORCESEEK)
        INNER JOIN @parked p ON p.job_id = r.job_id
        WHERE r.status_code IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */, 30 /* JobStatusCode.Paused */);

        INSERT INTO {{schema}}.events (
            event_code,
            created_at_utc,
            namespace_id,
            actor_code,
            actor_key,
            job_id,
            job_ref,
            execution_number,
            lineage_root_id,
            definition_id,
            tenant_id,
            worker_id,
            from_status_code,
            to_status_code,
            execution_status_code,
            duration_ms,
            reason_code,
            reason_message)
        SELECT
            70 /* EventCode.JobCancelled */,
            @now,
            p.namespace_id,
            @p_actor_code,
            @p_actor_key,
            p.job_id,
            p.job_ref,
            c.execution_number,
            p.lineage_root_id,
            p.definition_id,
            p.tenant_id,
            NULL,
            c.from_status_code,
            220 /* JobStatusCode.Cancelled */,
            NULL,
            NULL,
            42 /* JobEventReasonCode.JobDefinitionRetired */,
            @p_reason_message
        FROM @cancelled c
        INNER JOIN @parked p ON p.job_id = c.job_id
        WHERE p.audit_level_code = 20 /* JobAuditLevelCode.Audit */;

        INSERT INTO {{schema}}.events (
            event_code,
            created_at_utc,
            namespace_id,
            actor_code,
            actor_key,
            job_id,
            job_ref,
            execution_number,
            lineage_root_id,
            definition_id,
            tenant_id,
            worker_id,
            from_status_code,
            to_status_code,
            execution_status_code,
            duration_ms,
            reason_code,
            reason_message)
        VALUES (
            31 /* EventCode.JobDefinitionRetired */,
            @now,
            @ns,
            @p_actor_code,
            @p_actor_key,
            NULL,
            NULL,
            NULL,
            NULL,
            @p_id,
            NULL,
            NULL,
            NULL,
            NULL,
            NULL,
            NULL,
            @p_reason_code,
            @p_reason_message);
    END;

    SELECT
        @action AS action,
        c.job_id,
        p.parent_id
    FROM @cancelled c
    INNER JOIN @parked p ON p.job_id = c.job_id
    UNION ALL
    SELECT
        @action,
        CAST(NULL AS BIGINT),
        CAST(NULL AS BIGINT)
    WHERE NOT EXISTS (SELECT 1 FROM @cancelled);

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
