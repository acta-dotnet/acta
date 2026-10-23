CREATE OR ALTER PROCEDURE {{schema}}.access_checkpoint
    @p_action SMALLINT,
    @p_job_id BIGINT,
    @p_checkpoint_kind_code TINYINT,
    @p_checkpoint_name VARCHAR(128),
    @p_value_format_id TINYINT,
    @p_value VARBINARY(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        IF
            @p_action IN (10 /* AccessCheckpointAction.Set */, 30 /* AccessCheckpointAction.GetOrSet */)
            AND (@p_value_format_id = 0 /* JobPayloadFormat.None */ OR @p_value IS NULL)
            THROW 50002, 'Variable payload must use a non-zero format id and non-NULL value.', 1;

        -- The slot's own lock serializes the writers of one slot to commit (docs/sql-internals.md,
        -- "Slot locks"); every statement that may insert a checkpoint takes it under the same name.
        IF @p_action IN (10 /* AccessCheckpointAction.Set */, 30 /* AccessCheckpointAction.GetOrSet */)
            BEGIN
                DECLARE @slot_lock NVARCHAR(255) = CONCAT(N'{{schema}}.slot:', @p_job_id, N':', @p_checkpoint_kind_code, N':', @p_checkpoint_name);
                DECLARE @slot_lock_result INT;
                -- Positional: resource, mode, owner, timeout (-1 waits like any lock).
                EXEC @slot_lock_result = sp_getapplock @slot_lock, 'Exclusive', 'Transaction', -1;
                IF @slot_lock_result < 0
                    THROW 51205, 'ACTA:SLOT_LOCK:The checkpoint slot lock was not granted (deadlock victim or cancelled).', 1;
            END

        DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
        DECLARE @row TABLE (
            found INT NOT NULL,
            value_format_id TINYINT NULL,
            value VARBINARY(MAX) NULL,
            version INT NULL
        );

        IF @p_action = 10 /* AccessCheckpointAction.Set */
            BEGIN

                UPDATE jv
                SET
                    value_format_id = @p_value_format_id,
                    value = @p_value,
                    modified_at_utc = @now,
                    version = version + 1
                FROM {{schema}}.checkpoints AS jv WITH (UPDLOCK)
                WHERE
                    jv.job_id = @p_job_id
                    AND jv.checkpoint_kind_code = @p_checkpoint_kind_code
                    AND jv.checkpoint_name = @p_checkpoint_name;

                IF @@ROWCOUNT = 0
                    BEGIN
                        INSERT INTO {{schema}}.checkpoints (
                            job_id, checkpoint_kind_code, checkpoint_name,
                            value_format_id, value,
                            created_at_utc, modified_at_utc, version
                        )
                        VALUES (
                            @p_job_id, @p_checkpoint_kind_code, @p_checkpoint_name,
                            @p_value_format_id, @p_value,
                            @now, @now, 0
                        );
                    END

                INSERT INTO @row (found, value_format_id, value, version) VALUES (1, NULL, NULL, NULL);
            END
        ELSE IF @p_action = 20 /* AccessCheckpointAction.Get */
            BEGIN
                INSERT INTO @row (found, value_format_id, value, version)
                SELECT
                    1,
                    jv.value_format_id,
                    jv.value,
                    jv.version
                FROM {{schema}}.checkpoints AS jv
                WHERE
                    jv.job_id = @p_job_id
                    AND jv.checkpoint_kind_code = @p_checkpoint_kind_code
                    AND jv.checkpoint_name = @p_checkpoint_name;

                IF @@ROWCOUNT = 0
                    BEGIN
                        INSERT INTO @row (found, value_format_id, value, version) VALUES (0, NULL, NULL, NULL);
                    END
            END
        ELSE IF @p_action = 30 /* AccessCheckpointAction.GetOrSet */
            BEGIN

                INSERT INTO @row (found, value_format_id, value, version)
                SELECT
                    1,
                    jv.value_format_id,
                    jv.value,
                    jv.version
                FROM {{schema}}.checkpoints AS jv WITH (UPDLOCK)
                WHERE
                    jv.job_id = @p_job_id
                    AND jv.checkpoint_kind_code = @p_checkpoint_kind_code
                    AND jv.checkpoint_name = @p_checkpoint_name;

                IF @@ROWCOUNT = 0
                    BEGIN
                        INSERT INTO {{schema}}.checkpoints (
                            job_id, checkpoint_kind_code, checkpoint_name,
                            value_format_id, value,
                            created_at_utc, modified_at_utc, version
                        )
                        VALUES (
                            @p_job_id, @p_checkpoint_kind_code, @p_checkpoint_name,
                            @p_value_format_id, @p_value,
                            @now, @now, 0
                        );

                        INSERT INTO @row (found, value_format_id, value, version)
                        VALUES (1, @p_value_format_id, @p_value, 0);
                    END

            END
        ELSE IF @p_action = 40 /* AccessCheckpointAction.Exists */
            BEGIN
                INSERT INTO @row (found, value_format_id, value, version)
                SELECT
                    CASE WHEN EXISTS (
                        SELECT 1
                        FROM {{schema}}.checkpoints AS jv
                        WHERE
                            jv.job_id = @p_job_id
                            AND jv.checkpoint_kind_code = @p_checkpoint_kind_code
                            AND jv.checkpoint_name = @p_checkpoint_name
                    ) THEN 1 ELSE 0 END,
                    NULL,
                    NULL,
                    NULL;
            END
        ELSE IF @p_action = 50 /* AccessCheckpointAction.Delete */
            BEGIN
                DELETE FROM {{schema}}.checkpoints
                WHERE
                    job_id = @p_job_id
                    AND checkpoint_kind_code = @p_checkpoint_kind_code
                    AND checkpoint_name = @p_checkpoint_name;

                INSERT INTO @row (found, value_format_id, value, version)
                VALUES (CASE WHEN @@ROWCOUNT > 0 THEN 1 ELSE 0 END, NULL, NULL, NULL);
            END
        ELSE
            BEGIN
                THROW 50002, 'access_checkpoint received an unknown action.', 1;
            END

        SELECT
            found,
            value_format_id,
            value,
            version
        FROM @row;

        IF @entry_trancount = 0
            COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @entry_trancount = 0 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
