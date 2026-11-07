-- A latch, parent state, or child state another transaction holds is skipped and waits for the next pass. The child's
-- jobs row is never skipped: a skipped child would read as gone, and its latch would be released as Failed.
SELECT
    js.job_id AS parent_job_id,
    TRY_CAST(SUBSTRING(js.checkpoint_name, 11, 20) AS BIGINT) AS child_job_id,
    cr.job_status_code AS child_status
FROM {{schema}}.checkpoints js WITH (READCOMMITTEDLOCK, READPAST)
INNER JOIN {{schema}}.jobs p ON p.job_id = js.job_id AND p.namespace_id = @p_namespace_id
INNER JOIN {{schema}}.jobs_state pr WITH (READCOMMITTEDLOCK, READPAST) ON pr.job_id = js.job_id
LEFT JOIN {{schema}}.jobs c
    ON c.job_id =TRY_CAST(SUBSTRING(js.checkpoint_name, 11, 20) AS BIGINT)
LEFT JOIN {{schema}}.jobs_state cr WITH (READCOMMITTEDLOCK, READPAST) ON cr.job_id = c.job_id
WHERE
    js.checkpoint_kind_code = 50 /* CheckpointKindCode.ChildLatch */
    AND js.checkpoint_name LIKE 'sys.child.%'
    AND TRY_CAST(SUBSTRING(js.checkpoint_name, 11, 20) AS BIGINT) IS NOT NULL
    AND js.checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */
    -- A terminal parent waits on nothing, and a job that is not the parent's child never raises the latch.
    AND pr.job_status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
    AND (
        c.job_id IS NULL
        OR (
            c.parent_job_id = js.job_id
            AND cr.job_status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
        )
    );
