SELECT
    js.job_id AS parent_job_id,
    TRY_CAST(SUBSTRING(js.checkpoint_name, 11, 20) AS BIGINT) AS child_job_id,
    cr.job_status_code AS child_status,
    cr.execution_number AS child_execution_number
FROM {{schema}}.checkpoints js
INNER JOIN {{schema}}.jobs p ON p.job_id = js.job_id AND p.namespace_id = @p_namespace_id
LEFT JOIN {{schema}}.jobs c
    ON c.job_id =TRY_CAST(SUBSTRING(js.checkpoint_name, 11, 20) AS BIGINT)
LEFT JOIN {{schema}}.jobs_state cr ON cr.job_id = c.job_id
WHERE
    js.checkpoint_kind_code = 50 /* CheckpointKindCode.ChildLatch */
    AND js.checkpoint_name LIKE 'sys.child.%'
    AND TRY_CAST(SUBSTRING(js.checkpoint_name, 11, 20) AS BIGINT) IS NOT NULL
    AND js.checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */
    AND (c.job_id IS NULL OR cr.job_status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */));
