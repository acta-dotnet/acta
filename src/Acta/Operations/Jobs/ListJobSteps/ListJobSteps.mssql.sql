-- One keyset page of the job's steps, newest first; the memoized result stays the engine's.
SELECT TOP (@p_take)
    s.step_name AS name,
    s.step_status_code AS status,
    s.failure_message,
    s.created_at_utc,
    s.modified_at_utc
FROM {{schema}}.steps s
WHERE
    s.job_id = @p_job_id
    AND (
        @p_cursor_created_at_utc IS NULL
        OR s.created_at_utc < @p_cursor_created_at_utc
        OR (s.created_at_utc = @p_cursor_created_at_utc AND s.step_name < @p_cursor_step_name)
    )
ORDER BY s.created_at_utc DESC, s.step_name DESC;

SELECT CASE WHEN @p_include_total IS NOT NULL THEN (SELECT COUNT(*) FROM {{schema}}.steps s WHERE s.job_id = @p_job_id) END;
