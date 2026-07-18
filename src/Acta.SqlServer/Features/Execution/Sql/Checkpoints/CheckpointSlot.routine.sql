CREATE OR ALTER PROCEDURE {{schema}}.checkpoint_slot
    @p_action           SMALLINT,
    @p_job_id           BIGINT,
    @p_kind_code        TINYINT,
    @p_name             VARCHAR(128),
    @p_value_format_id  TINYINT,
    @p_value            VARBINARY(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @p_action IN (10 /* CheckpointSlotAction.Set */, 30 /* CheckpointSlotAction.GetOrSet */)
       AND (@p_value_format_id = 0 /* JobPayloadFormat.None */ OR @p_value IS NULL)
        THROW 50002, 'Variable payload must use a non-zero format id and non-NULL value.', 1;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @row TABLE (
        found           INT NOT NULL,
        value_format_id TINYINT NULL,
        value           VARBINARY(MAX) NULL,
        version         INT NULL
    );

    IF @p_action = 10 /* CheckpointSlotAction.Set */
    BEGIN
        BEGIN TRY
            BEGIN TRANSACTION;

            UPDATE jv
               SET value_format_id = @p_value_format_id,
                   value = @p_value,
                   modified_at_utc = @now,
                   version = version + 1
              FROM {{schema}}.checkpoints AS jv WITH (UPDLOCK, HOLDLOCK)
             WHERE jv.job_id = @p_job_id
               AND jv.kind_code = @p_kind_code
               AND jv.name = @p_name;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO {{schema}}.checkpoints (
                    job_id, kind_code, name,
                    value_format_id, value,
                    created_at_utc, modified_at_utc, version)
                VALUES (
                    @p_job_id, @p_kind_code, @p_name,
                    @p_value_format_id, @p_value,
                    @now, @now, 0);
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

        INSERT INTO @row (found, value_format_id, value, version) VALUES (1, NULL, NULL, NULL);
    END
    ELSE IF @p_action = 20 /* CheckpointSlotAction.Get */
    BEGIN
        INSERT INTO @row (found, value_format_id, value, version)
        SELECT 1, jv.value_format_id, jv.value, jv.version
          FROM {{schema}}.checkpoints AS jv
         WHERE jv.job_id = @p_job_id
           AND jv.kind_code = @p_kind_code
           AND jv.name = @p_name;

        IF @@ROWCOUNT = 0
        BEGIN
            INSERT INTO @row (found, value_format_id, value, version) VALUES (0, NULL, NULL, NULL);
        END
    END
    ELSE IF @p_action = 30 /* CheckpointSlotAction.GetOrSet */
    BEGIN
        BEGIN TRY
            BEGIN TRANSACTION;

            INSERT INTO @row (found, value_format_id, value, version)
            SELECT 1, jv.value_format_id, jv.value, jv.version
              FROM {{schema}}.checkpoints AS jv WITH (UPDLOCK, HOLDLOCK)
             WHERE jv.job_id = @p_job_id
               AND jv.kind_code = @p_kind_code
               AND jv.name = @p_name;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO {{schema}}.checkpoints (
                    job_id, kind_code, name,
                    value_format_id, value,
                    created_at_utc, modified_at_utc, version)
                VALUES (
                    @p_job_id, @p_kind_code, @p_name,
                    @p_value_format_id, @p_value,
                    @now, @now, 0);

                INSERT INTO @row (found, value_format_id, value, version)
                VALUES (1, @p_value_format_id, @p_value, 0);
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
    END
    ELSE IF @p_action = 40 /* CheckpointSlotAction.Exists */
    BEGIN
        INSERT INTO @row (found, value_format_id, value, version)
        SELECT CASE WHEN EXISTS (
            SELECT 1
              FROM {{schema}}.checkpoints AS jv
             WHERE jv.job_id = @p_job_id
               AND jv.kind_code = @p_kind_code
               AND jv.name = @p_name
        ) THEN 1 ELSE 0 END, NULL, NULL, NULL;
    END
    ELSE IF @p_action = 50 /* CheckpointSlotAction.Delete */
    BEGIN
        DELETE FROM {{schema}}.checkpoints
         WHERE job_id = @p_job_id
           AND kind_code = @p_kind_code
           AND name = @p_name;

        INSERT INTO @row (found, value_format_id, value, version)
        VALUES (CASE WHEN @@ROWCOUNT > 0 THEN 1 ELSE 0 END, NULL, NULL, NULL);
    END
    ELSE
    BEGIN
        THROW 50002, 'checkpoint_slot received an unknown action.', 1;
    END

    SELECT found, value_format_id, value, version FROM @row;
END;
GO
