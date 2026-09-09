-- Appends one application-authored job.note-recorded event; see IExecutionStore.RecordJobNoteAsync.
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
    reason_message
)
SELECT
    90 /* EventCode.JobNoteRecorded */,
    {{now}},
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

SELECT CHANGES() AS inserted;
