-- Appends one application-authored job.note-recorded event, written by ctx.NoteAsync.
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    INSERT INTO {{schema}}.events (
        event_code,
        created_at_utc,
        namespace_id,
        worker_id,
        entity_code,
        entity_id,
        definition_id,
        tenant_id,
        lineage_root_job_id,
        execution_number,
        event_message,
        details_format_id,
        details)
    SELECT
        90 /* EventCode.JobNoteRecorded */,
        SYSUTCDATETIME(),
        j.namespace_id,
        @p_worker_id,
        50 /* EntityCode.Job */,
        j.job_id,
        j.definition_id,
        j.tenant_id,
        COALESCE(j.lineage_root_job_id, j.job_id),
        @p_execution_number,
        @p_event_message,
        @p_details_format_id,
        @p_details
    FROM {{schema}}.jobs j
    WHERE j.job_id = @p_job_id;

    SELECT @@ROWCOUNT AS inserted;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
