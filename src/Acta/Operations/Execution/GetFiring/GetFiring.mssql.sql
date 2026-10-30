-- The attempt's firing envelope, only while the attempt still holds the job (docs/sql-internals.md, "Execution fence"):
-- a row for a slot with an envelope, none for a lost claim or an ordinary job. The firing's first attempt runs on the next
-- run its admission stamped with the envelope; a retry, a continuation, a reclaim, and a reschedule stamp a later one.
SELECT cp.value, CAST(CASE WHEN r.next_run_at_utc <= cp.modified_at_utc THEN 1 ELSE 0 END AS BIT) AS first_attempt
FROM {{schema}}.jobs_state r
INNER JOIN {{schema}}.checkpoints cp
    ON cp.job_id = r.job_id AND cp.checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */ AND cp.checkpoint_name = 'sys.firing'
WHERE
    r.job_id = @p_job_id
    AND r.job_status_code = 50 /* JobStatusCode.Executing */
    AND r.execution_number = @p_execution_number
    AND r.leased_by_worker_id = @p_leased_by_worker_id;
