-- Appends one application-authored job.note-recorded event; see IExecutionStore.RecordJobNoteAsync.

CREATE OR REPLACE FUNCTION {{schema}}.record_job_note(
    p_job_id BIGINT,
    p_execution_number INT,
    p_reason_message VARCHAR,
    p_detail_format_id SMALLINT,
    p_detail BYTEA
)
RETURNS TABLE (inserted INT)
LANGUAGE sql
AS $$
WITH inserted AS (
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
        now(),
        j.namespace_id,
        50 /* ActorCode.Job */,
        j.id,
        j.job_ref,
        p_execution_number,
        COALESCE(j.lineage_root_id, j.id),
        j.definition_id,
        j.tenant_id,
        p_detail_format_id,
        p_detail,
        p_reason_message
    FROM {{schema}}.jobs j
    WHERE j.id = p_job_id
    RETURNING 1
)
SELECT count(*)::INT AS inserted FROM inserted;
$$;
