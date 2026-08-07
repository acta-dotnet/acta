CREATE OR ALTER PROCEDURE {{schema}}.claim_one
    @p_namespace_id SMALLINT,
    @p_leased_by_worker_id INT,
    @p_lease_ttl_seconds INT,
    @p_id BIGINT,
    @p_start_executing BIT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @lease_expires DATETIME2(3) = DATEADD(SECOND, @p_lease_ttl_seconds, @now);
    /* Dueness compares at the column's DATETIME2(3) precision: enqueue rounds next_run_at_utc up to
       0.5 ms into the future, so a full-precision @now can transiently see a just-enqueued row as
       not yet due. Rounding is monotonic, so the same-precision comparison never can. */
    DECLARE @due_now DATETIME2(3) = @now;

    DECLARE
        @claimed TABLE
        (
            id BIGINT NOT NULL PRIMARY KEY,
            job_ref UNIQUEIDENTIFIER NOT NULL,
            namespace_id SMALLINT NOT NULL,
            lineage_root_id BIGINT NULL,
            definition_id INT NOT NULL,
            tenant_id INT NULL,
            execution_number INT NOT NULL,
            deduplication_key VARCHAR(128) NULL,
            correlation_key VARCHAR(64) NULL,
            exclusive_key VARCHAR(128) NULL,
            input_format_id TINYINT NOT NULL,
            input VARBINARY(MAX) NULL,
            next_run_at_utc DATETIME2(3) NULL,
            created_at_utc DATETIME2(3) NOT NULL,
            audit_level_code TINYINT NOT NULL,
            failure_count SMALLINT NOT NULL,
            version INT NOT NULL
        );

    BEGIN TRY
        BEGIN TRANSACTION;

        WITH candidates AS (
            SELECT r.job_id AS id
            FROM {{schema}}.runtimes r WITH (READPAST, UPDLOCK, ROWLOCK, READCOMMITTEDLOCK)
            WHERE
                r.job_id = @p_id
                AND r.namespace_id = @p_namespace_id
                AND r.status_code = 10 /* JobStatusCode.Ready */
                AND (r.next_run_at_utc IS NULL OR r.next_run_at_utc <= @due_now)
        )

        UPDATE r
        SET
            status_code = CASE WHEN @p_start_executing = 1 THEN 50 /* JobStatusCode.Executing */ ELSE 40 /* JobStatusCode.Dispatched */ END,
            execution_number = r.execution_number + 1,
            leased_by_worker_id = @p_leased_by_worker_id,
            lease_expires_at_utc = @lease_expires,
            modified_at_utc = @now,
            version = r.version + 1
        OUTPUT
            INSERTED.job_id, j.job_ref, j.namespace_id, j.lineage_root_id, j.definition_id, j.tenant_id,
            INSERTED.execution_number, j.deduplication_key, j.correlation_key, j.exclusive_key,
            j.input_format_id, j.input, INSERTED.next_run_at_utc, j.created_at_utc, j.audit_level_code,
            INSERTED.failure_count, INSERTED.version
        INTO
            @claimed (
                id, job_ref, namespace_id, lineage_root_id, definition_id, tenant_id, execution_number,
                deduplication_key, correlation_key, exclusive_key,
                input_format_id, input, next_run_at_utc, created_at_utc,
                audit_level_code, failure_count, version
            )
        FROM {{schema}}.runtimes r
        INNER JOIN candidates c ON c.id = r.job_id
        INNER JOIN {{schema}}.jobs j ON j.id = r.job_id;

        IF @p_start_executing = 1
            BEGIN
                INSERT INTO {{schema}}.events (
                    event_code, created_at_utc, namespace_id, actor_code, actor_key,
                    job_id, job_ref, execution_number,
                    lineage_root_id, definition_id, tenant_id, worker_id,
                    from_status_code, to_status_code, execution_status_code, duration_ms,
                    reason_code, reason_message
                )
                SELECT
                    40 /* JobEventCode.JobExecutionStarted */,
                    @now,
                    c.namespace_id,
                    70 /* JobActorCode.Worker */,
                    NULL,
                    c.id,
                    c.job_ref,
                    c.execution_number,
                    COALESCE(c.lineage_root_id, c.id),
                    c.definition_id,
                    c.tenant_id,
                    @p_leased_by_worker_id,
                    10 /* JobStatusCode.Ready */,
                    50 /* JobStatusCode.Executing */,
                    50 /* ExecutionStatusCode.Executing */,
                    NULL,
                    NULL,
                    NULL
                FROM @claimed c
                WHERE c.audit_level_code = 20 /* JobAuditLevelCode.Audit */;
            END

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            BEGIN
                ROLLBACK TRANSACTION;
            END;

        THROW;
    END CATCH;

    SELECT
        id,
        namespace_id,
        definition_id,
        execution_number,
        deduplication_key,
        correlation_key,
        exclusive_key,
        input_format_id,
        input,
        next_run_at_utc,
        @lease_expires AS lease_expires_at_utc,
        created_at_utc,
        failure_count,
        version,
        job_ref,
        tenant_id,
        CAST(NULL AS DATETIME2(7)) AS db_now,
        CAST(NULL AS DATETIME2(3)) AS next_ready_at_utc
    FROM @claimed;
END;
GO
