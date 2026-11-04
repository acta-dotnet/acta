-- One row with a null kind for a job without slots, none for a missing job; the handler orders the slots. The
-- scheduler's firing envelope is engine state, read by the job detail's firing view, not a handler slot.
SELECT
    c.checkpoint_kind_code AS kind,
    c.checkpoint_name,
    c.checkpoint_status_code AS status,
    c.due_at_utc,
    c.value_format_id,
    c.value,
    c.created_at_utc,
    c.modified_at_utc
FROM {{schema}}.jobs j
LEFT JOIN {{schema}}.checkpoints c ON c.job_id = j.job_id AND c.checkpoint_kind_code <> 60 /* CheckpointKindCode.Firing */
WHERE j.job_id = @p_job_id;
