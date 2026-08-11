-- Appends one application-authored job.note event; see IExecutionStore.RecordJobNoteAsync.
-- Denormalized columns are read from the job, so a note cannot disagree with the row it is about.
SELECT ACTA_ERROR('ACTA:NOTE_UNKNOWN_JOB:record_job_note: unknown job id')
WHERE NOT EXISTS (SELECT 1 FROM {{schema}}.jobs WHERE id = @p_job_id);

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
    90 /* JobEventCode.JobNoteRecorded */,
    {{now}},
    j.namespace_id,
    50 /* JobActorCode.Job */,
    j.id,
    j.job_ref,
    r.execution_number,
    COALESCE(j.lineage_root_id, j.id),
    j.definition_id,
    j.tenant_id,
    @p_detail_format_id,
    @p_detail,
    @p_reason_message
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
WHERE j.id = @p_job_id;
