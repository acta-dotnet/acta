CREATE OR ALTER PROCEDURE {{schema}}.start_execution
    @p_id                   BIGINT,
    @p_leased_by_worker_id  INT,
    @p_execution_number     INT,
    @p_version              INT,
    @p_lease_ttl_seconds    INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @action TINYINT;
    DECLARE @started TABLE (
        id                BIGINT           NOT NULL,
        job_ref           UNIQUEIDENTIFIER NOT NULL,
        namespace_id  SMALLINT         NOT NULL,
        lineage_root_id   BIGINT           NULL,
        definition_id INT              NOT NULL,
        tenant_id         INT              NULL,
        execution_number  INT              NOT NULL,
        audit_level_code  TINYINT          NOT NULL);

    BEGIN TRY
        BEGIN TRANSACTION;

        UPDATE r
           SET status_code          = 50 /* JobStatusCode.Executing */,
               lease_expires_at_utc = DATEADD(SECOND, @p_lease_ttl_seconds, @now),
               modified_at_utc      = @now,
               version              = r.version + 1
        OUTPUT inserted.job_id,
               j.job_ref,
               j.namespace_id,
               j.lineage_root_id,
               j.definition_id,
               j.tenant_id,
               inserted.execution_number,
               j.audit_level_code
          INTO @started
          FROM {{schema}}.runtimes r
          INNER JOIN {{schema}}.jobs j ON j.id = r.job_id
         WHERE r.job_id             = @p_id
           AND r.execution_number   = @p_execution_number
           AND r.version            = @p_version
           AND r.status_code        = 40 /* JobStatusCode.Dispatched */
           AND r.leased_by_worker_id = @p_leased_by_worker_id
           AND r.lease_expires_at_utc > @now;

        IF EXISTS (SELECT 1 FROM @started)
        BEGIN
            SET @action = 1 /* StartExecutionAction.Started */;

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
                40 /* JobEventCode.JobExecutionStarted */, @now, st.namespace_id,
                70 /* JobActorCode.Worker */, NULL,
                st.id, st.job_ref, st.execution_number,
                COALESCE(st.lineage_root_id, st.id), st.definition_id, st.tenant_id,
                @p_leased_by_worker_id,
                40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */,
                50 /* ExecutionStatusCode.Executing */, NULL,
                NULL, NULL
              FROM @started st
             WHERE st.audit_level_code = 20 /* JobAuditLevelCode.Audit */;
        END
        ELSE
        BEGIN
            DECLARE @curWorker INT, @curEn INT, @curStatus TINYINT, @curVersion INT, @curLease DATETIME2(3);
            SELECT @curEn      = r.execution_number,
                   @curStatus  = r.status_code,
                   @curVersion = r.version,
                   @curWorker  = r.leased_by_worker_id,
                   @curLease   = r.lease_expires_at_utc
              FROM {{schema}}.runtimes r
             WHERE r.job_id = @p_id;

            SET @action = CASE
                WHEN @curStatus IS NULL
                  OR @curStatus IN (
                        100 /* JobStatusCode.Succeeded */,
                        200 /* JobStatusCode.Failed */,
                        220 /* JobStatusCode.Cancelled */
                    )
                    THEN 4 /* StartExecutionAction.AlreadyTerminal */
                WHEN @curWorker <> @p_leased_by_worker_id OR @curWorker IS NULL
                    THEN 2 /* StartExecutionAction.NotOwner */
                WHEN @curEn <> @p_execution_number
                    THEN 3 /* StartExecutionAction.LostClaim */
                WHEN @curVersion <> @p_version
                    THEN 3 /* StartExecutionAction.LostClaim */
                WHEN @curStatus = 40 /* JobStatusCode.Dispatched */ AND @curLease <= @now
                    THEN 5 /* StartExecutionAction.LeaseExpired */
                ELSE 4 /* StartExecutionAction.AlreadyTerminal */
            END;
        END

        COMMIT TRANSACTION;

        SELECT @action;
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
