SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    -- A raise's lock order: the signal's checkpoint lock, then the job's row (docs/sql-internals.md, "Step, checkpoint
    -- and rate locks"), so a clear and a raise of one signal queue instead of deadlocking.
    DECLARE @checkpoint_lock NVARCHAR(255) = CONCAT(N'{{schema}}.checkpoint:', @p_job_id, N':20:', @p_checkpoint_name);
    DECLARE @checkpoint_lock_result INT;
    -- Positional: resource, mode, owner, timeout (-1 waits like any lock).
    EXEC @checkpoint_lock_result = sp_getapplock @checkpoint_lock, 'Exclusive', 'Transaction', -1;
    IF @checkpoint_lock_result < 0
        THROW 51205, 'ACTA:CHECKPOINT_LOCK:The checkpoint lock was not granted (deadlock victim or cancelled).', 1;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE
        @status TINYINT, @namespace_id INT,
        @lineage_root_job_id BIGINT, @definition_id INT, @tenant_id INT, @execution_number BIGINT;
    DECLARE @cleared INT = 0;

    SELECT
        @status = r.job_status_code,
        @namespace_id = j.namespace_id,
        @lineage_root_job_id = j.lineage_root_job_id,
        @definition_id = j.definition_id,
        @tenant_id = j.tenant_id,
        @execution_number = r.execution_number
    FROM {{schema}}.jobs_state r WITH (UPDLOCK, ROWLOCK)
    INNER JOIN {{schema}}.jobs j ON j.job_id = r.job_id
    WHERE r.job_id = @p_job_id;

    -- A finished job, or a permanent job between firings, forgets the signal, so its next wait for it waits again.
    IF @status IN (
        40 /* JobStatusCode.Idle */, 100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */
    )
        BEGIN
            DELETE FROM {{schema}}.checkpoints WITH (ROWLOCK)
            WHERE
                job_id = @p_job_id
                AND checkpoint_kind_code = 20 /* CheckpointKindCode.Signal */
                AND checkpoint_name = @p_checkpoint_name;
            SET @cleared = @@ROWCOUNT;
        END;

    IF @cleared > 0
        BEGIN
            INSERT INTO {{schema}}.events (
                event_code, created_at_utc, namespace_id,
                entity_code, entity_id,
                definition_id, tenant_id, lineage_root_job_id,
                execution_number,
                event_message, actor, details_format_id, details
            )
            VALUES (
                82 /* EventCode.JobSignalCleared */, @now, @namespace_id,
                50 /* EntityCode.Job */, @p_job_id,
                @definition_id, @tenant_id, COALESCE(@lineage_root_job_id, @p_job_id),
                @execution_number,
                @p_event_message, @p_actor, @p_details_format_id, @p_details
            );
        END;

    SELECT
        CAST(
            CASE
                WHEN @status IS NULL THEN 2 /* ControlAction.NotFound */
                WHEN @cleared > 0 THEN 1 /* ControlAction.Applied */
                WHEN @status IN (
                    40 /* JobStatusCode.Idle */, 100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */
                ) THEN 6 /* ControlAction.AlreadyInState */
                ELSE 3 /* ControlAction.Rejected */
            END AS TINYINT
        ) AS action,
        @status AS status;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
