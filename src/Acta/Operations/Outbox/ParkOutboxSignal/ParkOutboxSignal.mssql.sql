-- Park admission for the sys.outbox operator inbox: insert when the slot is free, supersede when the
-- pending command has outlived the worker-dead window, reject otherwise.
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @modified DATETIME2(7);

    -- The slot's own lock, as wait_signal names it (docs/sql-execution-policy.md, "Slot locks").
    DECLARE @slot_lock NVARCHAR(255) = CONCAT(N'{{schema}}.slot:', @p_job_id, N':20:', @p_checkpoint_name);
    DECLARE @slot_lock_result INT;
    -- Positional: resource, mode, owner, timeout (-1 waits like any lock).
    EXEC @slot_lock_result = sp_getapplock @slot_lock, 'Exclusive', 'Transaction', -1;
    IF @slot_lock_result < 0
        THROW 51205, 'ACTA:SLOT_LOCK:The checkpoint slot lock was not granted (deadlock victim or cancelled).', 1;

    SELECT @modified = modified_at_utc
    FROM {{schema}}.checkpoints WITH (UPDLOCK)
    WHERE job_id = @p_job_id AND checkpoint_kind_code = 20 /* CheckpointKindCode.Signal */ AND checkpoint_name = @p_checkpoint_name;

    IF @modified IS NULL
        INSERT INTO {{schema}}.checkpoints (
            job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code, value_format_id, value,
            created_at_utc, modified_at_utc, version
        )
        VALUES (
            @p_job_id, 20 /* CheckpointKindCode.Signal */, @p_checkpoint_name, 20 /* CheckpointStatusCode.Set */,
            @p_value_format_id, @p_value, @now, @now, 0
        );
    ELSE IF @modified <= @p_stale_before_utc
        UPDATE {{schema}}.checkpoints
        SET
            checkpoint_status_code = 20 /* CheckpointStatusCode.Set */,
            value_format_id = @p_value_format_id,
            value = @p_value,
            modified_at_utc = @now,
            version = version + 1
        WHERE job_id = @p_job_id AND checkpoint_kind_code = 20 /* CheckpointKindCode.Signal */ AND checkpoint_name = @p_checkpoint_name;

    /* The minted command id inside @p_value makes the payload unique, so value equality is the
       "my write landed" test; a losing park reads the incumbent's park instant as the rejection age. */
    SELECT
        CAST(CASE WHEN c.value = @p_value THEN 1 /* ControlAction.Applied */ ELSE 3 /* ControlAction.Rejected */ END AS SMALLINT)
            AS action,
        c.modified_at_utc AS pending_since_utc
    FROM {{schema}}.checkpoints c
    WHERE c.job_id = @p_job_id AND c.checkpoint_kind_code = 20 /* CheckpointKindCode.Signal */ AND c.checkpoint_name = @p_checkpoint_name;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
