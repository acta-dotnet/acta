-- One row with a null kind for a job without slots, none for a missing job; CheckpointSet orders the slots.
SELECT
    c.kind_code,
    c.name,
    c.status_code,
    c.due_at_utc,
    c.value_format_id,
    c.value,
    c.created_at_utc,
    c.modified_at_utc
FROM {{schema}}.jobs j
LEFT JOIN {{schema}}.checkpoints c ON c.job_id = j.id
WHERE j.id = @p_job_id;
