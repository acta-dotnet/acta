-- Appends one application-authored job.note-recorded event; see IExecutionStore.RecordJobNoteAsync.
-- Denormalized columns are read from the job, so a note cannot disagree with the row it is about.
CREATE OR REPLACE FUNCTION {{schema}}.record_job_note(
    p_job_id BIGINT,
    p_reason_message VARCHAR,
    p_detail_format_id SMALLINT,
    p_detail BYTEA
)
RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
    v_rows INT;
BEGIN
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
        r.execution_number,
        COALESCE(j.lineage_root_id, j.id),
        j.definition_id,
        j.tenant_id,
        p_detail_format_id,
        p_detail,
        p_reason_message
    FROM {{schema}}.jobs j
    JOIN {{schema}}.runtimes r ON r.job_id = j.id
    WHERE j.id = p_job_id;

    GET DIAGNOSTICS v_rows = ROW_COUNT;

    IF v_rows = 0 THEN
        RAISE EXCEPTION 'ACTA:NOTE_UNKNOWN_JOB:record_job_note: unknown job id'
            USING ERRCODE = 'P0001';
    END IF;
END;
$$;
