-- One keyset page of the job's slots by kind, then name. The scheduler's firing envelope is engine state, read by the
-- job detail's firing view, not a handler slot.
SELECT TOP (@p_take)
    c.checkpoint_kind_code AS kind,
    c.checkpoint_name,
    c.checkpoint_status_code AS status,
    c.due_at_utc,
    c.value_format_id,
    c.value,
    c.created_at_utc,
    c.modified_at_utc
FROM {{schema}}.checkpoints c
WHERE
    c.job_id = @p_job_id
    AND c.checkpoint_kind_code <> 60 /* CheckpointKindCode.Firing */
    AND (
        @p_cursor_kind IS NULL
        OR c.checkpoint_kind_code > @p_cursor_kind
        OR (c.checkpoint_kind_code = @p_cursor_kind AND c.checkpoint_name > @p_cursor_checkpoint_name)
    )
ORDER BY c.checkpoint_kind_code, c.checkpoint_name;

SELECT CASE WHEN @p_include_total IS NOT NULL THEN (
    SELECT COUNT(*)
    FROM {{schema}}.checkpoints c
    WHERE c.job_id = @p_job_id AND c.checkpoint_kind_code <> 60 /* CheckpointKindCode.Firing */
) END;
