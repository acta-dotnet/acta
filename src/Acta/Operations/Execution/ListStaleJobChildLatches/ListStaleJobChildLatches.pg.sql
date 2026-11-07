SELECT
    js.job_id AS parent_job_id,
    CASE WHEN js.checkpoint_name ~ '^sys\.child\.[0-9]+$' THEN substring(js.checkpoint_name from 11)::bigint END AS child_job_id,
    cr.job_status_code AS child_status
FROM {{schema}}.checkpoints js
INNER JOIN {{schema}}.jobs p ON p.job_id = js.job_id AND p.namespace_id = @p_namespace_id
INNER JOIN {{schema}}.jobs_state pr ON pr.job_id = js.job_id
LEFT JOIN {{schema}}.jobs c
    ON c.job_id =CASE WHEN js.checkpoint_name ~ '^sys\.child\.[0-9]+$' THEN substring(js.checkpoint_name from 11)::bigint END
LEFT JOIN {{schema}}.jobs_state cr ON cr.job_id = c.job_id
WHERE
    js.checkpoint_kind_code = 50 /* CheckpointKindCode.ChildLatch */
    AND js.checkpoint_name ~ '^sys\.child\.[0-9]+$'
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
