CREATE OR ALTER PROCEDURE {{schema}}.complete_executions_batch
    @p_batch {{schema}}.complete_executions_batch READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();

    DECLARE @updated TABLE (
        ordinal INT NOT NULL PRIMARY KEY,
        job_id BIGINT NOT NULL,
        execution_number INT NOT NULL,
        job_ref UNIQUEIDENTIFIER NOT NULL,
        namespace_id SMALLINT NOT NULL,
        lineage_root_id BIGINT NULL,
        definition_id INT NOT NULL,
        tenant_id INT NULL,
        audit_level_code TINYINT NOT NULL
    );

    BEGIN TRY
        BEGIN TRANSACTION;

        UPDATE r
        SET
            status_code = CASE WHEN b.succeeded = 1 THEN 100 /* JobStatusCode.Succeeded */ ELSE 200 /* JobStatusCode.Failed */ END,
            failure_count = COALESCE(b.failure_count, r.failure_count),
            leased_by_worker_id = NULL,
            lease_expires_at_utc = NULL,
            retention_until_utc = CASE
                WHEN b.retention_seconds IS NOT NULL
                    THEN DATEADD(SECOND, b.retention_seconds, @now)
                ELSE r.retention_until_utc
            END,
            modified_at_utc = @now,
            version = r.version + 1
        OUTPUT
            b.ordinal, INSERTED.job_id, INSERTED.execution_number, j.job_ref, j.namespace_id,
            j.lineage_root_id, j.definition_id, j.tenant_id, j.audit_level_code
        INTO @updated
        FROM {{schema}}.runtimes r
        INNER JOIN {{schema}}.jobs j ON j.id = r.job_id
        INNER JOIN @p_batch b
            ON
                b.job_id = r.job_id
                AND b.execution_number = r.execution_number
        WHERE
            r.status_code = 50 /* JobStatusCode.Executing */
            AND j.parent_id IS NULL
            AND r.leased_by_worker_id = b.worker_id;

        INSERT INTO {{schema}}.results (job_id, execution_number, result_format_id, result, created_at_utc)
        SELECT
            u.job_id,
            u.execution_number,
            b.result_format_id,
            b.result,
            @now
        FROM @updated u
        INNER JOIN @p_batch b ON b.ordinal = u.ordinal
        WHERE b.result_format_id <> 0 /* JobPayloadFormat.None */;

        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id, actor_code, actor_key,
            job_id, job_ref, execution_number, lineage_root_id, definition_id, tenant_id,
            worker_id, from_status_code, to_status_code, execution_status_code, duration_ms,
            reason_code, reason_message
        )
        SELECT
            41 /* JobEventCode.JobExecutionFinished */,
            @now,
            u.namespace_id,
            70 /* JobActorCode.Worker */,
            NULL,
            u.job_id,
            u.job_ref,
            u.execution_number,
            COALESCE(u.lineage_root_id, u.job_id),
            u.definition_id,
            u.tenant_id,
            b.worker_id,
            50 /* JobStatusCode.Executing */,
            CASE WHEN b.succeeded = 1 THEN 100 /* JobStatusCode.Succeeded */ ELSE 200 /* JobStatusCode.Failed */ END,
            CASE WHEN b.succeeded = 1 THEN 100 /* ExecutionStatusCode.Succeeded */ ELSE 200 /* ExecutionStatusCode.Failed */ END,
            b.duration_ms,
            b.reason_code,
            b.reason_message
        FROM @updated u
        INNER JOIN @p_batch b ON b.ordinal = u.ordinal
        WHERE
            u.audit_level_code = 20 /* JobAuditLevelCode.Audit */
            OR (u.audit_level_code = 10 /* JobAuditLevelCode.Failures */ AND b.succeeded = 0);

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
        b.ordinal,
        CAST(CASE WHEN u.ordinal IS NOT NULL THEN 1 ELSE 0 END AS SMALLINT) AS finalized
    FROM @p_batch b
    LEFT JOIN @updated u ON u.ordinal = b.ordinal
    ORDER BY b.ordinal;
END;
