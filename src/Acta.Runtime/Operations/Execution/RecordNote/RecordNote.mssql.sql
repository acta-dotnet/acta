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
        actor_code,
        job_id,
        job_ref,
        execution_number,
        lineage_root_id,
        definition_id,
        tenant_id,
        detail_format_id,
        detail,
        reason_message)
    SELECT
        90 /* EventCode.JobNoteRecorded */,
        SYSUTCDATETIME(),
        j.namespace_id,
        50 /* ActorCode.Job */,
        j.id,
        j.job_ref,
        @p_execution_number,
        COALESCE(j.lineage_root_id, j.id),
        j.definition_id,
        j.tenant_id,
        @p_detail_format_id,
        @p_detail,
        @p_reason_message
    FROM {{schema}}.jobs j
    WHERE j.id = @p_job_id;

    SELECT @@ROWCOUNT AS inserted;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
