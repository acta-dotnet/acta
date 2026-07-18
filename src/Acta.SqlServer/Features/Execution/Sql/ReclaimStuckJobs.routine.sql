CREATE OR ALTER PROCEDURE {{schema}}.reclaim_stuck_jobs
    @p_namespace_id SMALLINT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();

    DECLARE @reclaimed TABLE
    (
        id                BIGINT           NOT NULL PRIMARY KEY,
        job_ref           UNIQUEIDENTIFIER NOT NULL,
        namespace_id  SMALLINT         NOT NULL,
        execution_number  INT              NOT NULL,
        lineage_root_id   BIGINT           NULL,
        definition_id INT              NOT NULL,
        tenant_id         INT              NULL,
        from_status_code  TINYINT          NOT NULL,
        to_status_code    TINYINT          NOT NULL,
        audit_level_code  TINYINT          NOT NULL,
        parent_id         BIGINT           NULL
    );

    BEGIN TRY
        BEGIN TRANSACTION;

        WITH stuck AS (
            SELECT r.job_id AS id,
                   r.status_code          AS from_status,
                   (r.failure_count + 1)  AS new_failure_count,
                   jd.max_attempts_effective AS max_attempts,
                   jd.retention_seconds_effective AS retention_seconds
              FROM {{schema}}.runtimes r WITH (READPAST, UPDLOCK, ROWLOCK)
              INNER JOIN {{schema}}.jobs j ON j.id = r.job_id
              INNER JOIN {{schema}}.definitions jd ON jd.id = j.definition_id
             WHERE r.status_code IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */)
               AND r.lease_expires_at_utc < @now
               AND r.namespace_id = @p_namespace_id
        )
        UPDATE r
           SET status_code          = CASE WHEN s.new_failure_count >= s.max_attempts
                                             THEN 200 /* JobStatusCode.Failed */
                                             ELSE 10  /* JobStatusCode.Ready */ END,
               failure_count        = s.new_failure_count,
               next_run_at_utc      = CASE WHEN s.new_failure_count >= s.max_attempts
                                             THEN r.next_run_at_utc ELSE @now END,
               leased_by_worker_id  = NULL,
               lease_expires_at_utc = NULL,
               retention_until_utc  = CASE WHEN s.new_failure_count >= s.max_attempts
                                             THEN DATEADD(SECOND, s.retention_seconds, @now)
                                             ELSE r.retention_until_utc END,
               modified_at_utc      = @now,
               version              = r.version + 1
        OUTPUT inserted.job_id, j.job_ref, j.namespace_id, inserted.execution_number,
               j.lineage_root_id, j.definition_id, j.tenant_id,
               deleted.status_code, inserted.status_code, j.audit_level_code,
               j.parent_id
          INTO @reclaimed (id, job_ref, namespace_id, execution_number, lineage_root_id,
               definition_id, tenant_id, from_status_code, to_status_code, audit_level_code, parent_id)
          FROM {{schema}}.runtimes r
          INNER JOIN stuck s ON s.id = r.job_id
          INNER JOIN {{schema}}.jobs j ON j.id = r.job_id;

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
            41 /* JobEventCode.JobExecutionFinished */, @now, namespace_id,
            10 /* JobActorCode.Sys */, NULL,
            id, job_ref, execution_number,
            COALESCE(lineage_root_id, id), definition_id, tenant_id,
            NULL,
            from_status_code, to_status_code,
            230 /* ExecutionStatusCode.Orphaned */, NULL,
            21 /* JobEventReasonCode.JobLeaseExpired */, N'Worker lease expired; reclaimed by the sys.recovery system job.'
          FROM @reclaimed
         WHERE audit_level_code IN (10 /* JobAuditLevelCode.Failures */, 20 /* JobAuditLevelCode.Audit */);

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
        BEGIN
            ROLLBACK TRANSACTION;
        END;

        THROW;
    END CATCH;

    SELECT id AS job_id, to_status_code AS to_status, parent_id FROM @reclaimed;
END;
GO
