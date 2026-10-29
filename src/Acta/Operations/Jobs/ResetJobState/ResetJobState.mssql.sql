SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE
        @status TINYINT, @namespace_id INT,
        @lineage_root_job_id BIGINT, @definition_id INT, @tenant_id INT, @execution_number INT, @event_recording TINYINT;

    SELECT
        @status = r.job_status_code,
        @namespace_id = j.namespace_id,
        @lineage_root_job_id = j.lineage_root_job_id,
        @definition_id = j.definition_id,
        @tenant_id = j.tenant_id,
        @execution_number = r.execution_number,
        @event_recording = j.event_recording_code
    FROM {{schema}}.jobs_state r WITH (UPDLOCK, ROWLOCK)
    INNER JOIN {{schema}}.jobs j ON j.job_id = r.job_id
    WHERE
        r.job_id = @p_job_id
        AND r.job_status_code = 50 /* JobStatusCode.Executing */
        AND r.execution_number = @p_execution_number
        AND r.leased_by_worker_id = @p_leased_by_worker_id;

    -- The reset clears nothing unless the attempt still holds the row, update-locked to commit (docs/sql-internals.md,
    -- "Execution fence"); the answer says which.
    IF @status IS NULL
        BEGIN

            GOTO Finish;
        END;

    -- The scheduler's firing envelope is not the handler's state and stays.
    DELETE FROM {{schema}}.checkpoints
    WHERE job_id = @p_job_id AND checkpoint_kind_code <> 60 /* CheckpointKindCode.Firing */;
    DELETE FROM {{schema}}.steps
    WHERE job_id = @p_job_id;

    -- The job's own handler asked for the reset, so the job id is the actor. Digits need no JSON escaping, and
    -- pure ASCII makes VARCHAR -> VARBINARY UTF-8.
    IF @event_recording = 20 /* EventRecordingCode.Audit */
        BEGIN
            INSERT INTO {{schema}}.events (
                event_code, created_at_utc, namespace_id,
                entity_code, entity_id,
                definition_id, tenant_id, lineage_root_job_id,
                execution_number,
                details_format_id, details
            )
            VALUES (
                81 /* EventCode.JobStateReset */, @now, @namespace_id,
                50 /* EntityCode.Job */, @p_job_id,
                @definition_id, @tenant_id, COALESCE(@lineage_root_job_id, @p_job_id),
                @execution_number,
                1 /* JobPayloadFormat.Json */, CONVERT(VARBINARY(MAX), '{"actor":"' + CONVERT(VARCHAR(20), @p_job_id) + '"}')
            );
        END

Finish:

    SELECT CASE WHEN @status IS NULL THEN 0 ELSE 1 END AS applied;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
