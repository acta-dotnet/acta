-- Deletes the command the tick read, matched by its stored value, which carries the command id minted at park, and
-- only when it did writes the evidence event, so one command leaves one event. Any other command under the name, a
-- superseding one or one parked at the same version after another pass consumed it, survives for the next tick.
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DELETE FROM {{schema}}.checkpoints
    WHERE
        job_id = @p_job_id
        AND checkpoint_kind_code = 20 /* CheckpointKindCode.Signal */
        AND checkpoint_name = @p_checkpoint_name
        AND value = @p_value;

    DECLARE @consumed BIGINT = @@ROWCOUNT;

    IF @consumed > 0
        INSERT INTO {{schema}}.events (
            event_code,
            created_at_utc,
            namespace_id,
            entity_code,
            entity_id,
            definition_id,
            tenant_id,
            lineage_root_job_id,
            execution_number,
            event_message,
            details_format_id,
            details
        )
        SELECT
            @p_event_code,
            SYSUTCDATETIME(),
            j.namespace_id,
            50 /* EntityCode.Job */,
            j.job_id,
            j.definition_id,
            j.tenant_id,
            COALESCE(j.lineage_root_job_id, j.job_id),
            r.execution_number,
            @p_event_message,
            @p_details_format_id,
            @p_details
        FROM {{schema}}.jobs j
        INNER JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
        WHERE j.job_id = @p_job_id;

    SELECT @consumed AS consumed;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
