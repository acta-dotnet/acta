-- Park admission for the sys.outbox operator inbox: insert when the slot is free, supersede when the
-- pending command has outlived the worker-dead window, reject otherwise. See IOutboxSignalStore.ParkAsync.
CREATE OR ALTER PROCEDURE {{schema}}.park_outbox_signal
    @p_job_id BIGINT,
    @p_name VARCHAR(128),
    @p_value_format_id TINYINT,
    @p_value VARBINARY(MAX),
    @p_stale_before_utc DATETIME2(7)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @modified DATETIME2(7);

    BEGIN TRANSACTION;

    SELECT @modified = modified_at_utc
    FROM {{schema}}.checkpoints WITH (UPDLOCK, HOLDLOCK)
    WHERE job_id = @p_job_id AND kind_code = 20 /* JobCheckpointKindCode.Signal */ AND name = @p_name;

    IF @modified IS NULL
        INSERT INTO {{schema}}.checkpoints (
            job_id, kind_code, name, status_code, value_format_id, value,
            created_at_utc, modified_at_utc, version
        )
        VALUES (
            @p_job_id, 20 /* JobCheckpointKindCode.Signal */, @p_name, 20 /* JobCheckpointStatusCode.Set */,
            @p_value_format_id, @p_value, @now, @now, 0
        );
    ELSE IF @modified <= @p_stale_before_utc
        UPDATE {{schema}}.checkpoints
        SET
            status_code = 20 /* JobCheckpointStatusCode.Set */,
            value_format_id = @p_value_format_id,
            value = @p_value,
            modified_at_utc = @now,
            version = version + 1
        WHERE job_id = @p_job_id AND kind_code = 20 /* JobCheckpointKindCode.Signal */ AND name = @p_name;

    COMMIT TRANSACTION;

    /* The minted command id inside @p_value makes the payload unique, so value equality is the
       "my write landed" test; a losing park reads the incumbent's park instant as the rejection age. */
    SELECT
        CAST(CASE WHEN c.value = @p_value THEN 1 /* ControlAction.Applied */ ELSE 3 /* ControlAction.Rejected */ END AS SMALLINT)
            AS action,
        c.modified_at_utc AS pending_since_utc
    FROM {{schema}}.checkpoints c
    WHERE c.job_id = @p_job_id AND c.kind_code = 20 /* JobCheckpointKindCode.Signal */ AND c.name = @p_name;
END
