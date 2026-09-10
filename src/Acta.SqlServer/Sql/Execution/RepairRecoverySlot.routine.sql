-- Re-arms one namespace's sys.recovery slot when it is stranded; see IExecutionStore.RepairRecoverySlotAsync.
CREATE OR ALTER PROCEDURE {{schema}}.repair_recovery_slot
    @p_namespace_id INT,
    @p_job_id BIGINT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
        DECLARE @repaired TABLE (job_id BIGINT NOT NULL PRIMARY KEY, execution_number INT NOT NULL, from_status TINYINT NOT NULL);

        UPDATE r
        SET
            status_code = 10 /* JobStatusCode.Ready */,
            next_run_at_utc = @now,
            failure_count = CASE WHEN r.failure_count + 1 > 32767 THEN 32767 ELSE r.failure_count + 1 END,
            leased_by_worker_id = NULL,
            lease_expires_at_utc = NULL,
            modified_at_utc = @now,
            version = r.version + 1
        OUTPUT inserted.job_id, deleted.execution_number, deleted.status_code INTO @repaired (job_id, execution_number, from_status)
        FROM {{schema}}.runtimes r WITH (FORCESEEK)
        WHERE
            r.job_id = @p_job_id
            AND r.namespace_id = @p_namespace_id
            AND r.status_code IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */)
            AND r.lease_expires_at_utc IS NOT NULL
            AND r.lease_expires_at_utc < @now;

        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id,
            actor_code, actor_key,
            job_id, job_ref, execution_number,
            lineage_root_id, definition_id, tenant_id,
            worker_id,
            from_status_code, to_status_code,
            execution_status_code, duration_ms,
            reason_code, reason_message
        )
        SELECT
            41 /* EventCode.JobExecutionFinished */, @now, j.namespace_id,
            10 /* ActorCode.Sys */, NULL,
            j.id, j.job_ref, x.execution_number,
            COALESCE(j.lineage_root_id, j.id), j.definition_id, j.tenant_id,
            NULL,
            x.from_status, 10 /* JobStatusCode.Ready */,
            230 /* ExecutionStatusCode.Orphaned */, NULL,
            21 /* JobEventReasonCode.JobLeaseExpired */,
            N'Worker lease expired on the recovery slot; re-armed by a worker''s recovery monitor.'
        FROM @repaired x
        INNER JOIN {{schema}}.jobs j ON j.id = x.job_id;

        SELECT CASE
            WHEN EXISTS (SELECT 1 FROM @repaired) THEN 2 /* RecoverySlotRepair.Repaired */
            WHEN EXISTS (SELECT 1 FROM {{schema}}.runtimes r WHERE r.job_id = @p_job_id AND r.namespace_id = @p_namespace_id) THEN 1 /* RecoverySlotRepair.Healthy */
            ELSE 0 /* RecoverySlotRepair.Missing */ END AS outcome;

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
