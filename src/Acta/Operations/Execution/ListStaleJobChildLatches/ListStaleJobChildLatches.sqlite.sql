SELECT
    js.job_id AS parent_job_id,
    CASE WHEN SUBSTR(js.checkpoint_name, 11) GLOB '*[^0-9]*' OR SUBSTR(js.checkpoint_name, 11) = '' THEN NULL ELSE CAST(SUBSTR(js.checkpoint_name, 11) AS INTEGER) END
        AS child_job_id,
    cr.job_status_code AS child_status,
    cr.execution_number AS child_execution_number
FROM {{schema}}.checkpoints js
INNER JOIN {{schema}}.jobs p ON p.job_id = js.job_id AND p.namespace_id = @p_namespace_id
LEFT JOIN
    {{schema}}.jobs c
    ON
        c.job_id
        = CASE WHEN SUBSTR(js.checkpoint_name, 11) GLOB '*[^0-9]*' OR SUBSTR(js.checkpoint_name, 11) = '' THEN NULL ELSE CAST(SUBSTR(js.checkpoint_name, 11) AS INTEGER) END
LEFT JOIN {{schema}}.jobs_state cr ON cr.job_id = c.job_id
WHERE
    js.checkpoint_kind_code = 50 /* CheckpointKindCode.ChildLatch */
    AND js.checkpoint_name LIKE 'sys.child.%'
    AND SUBSTR(js.checkpoint_name, 11) <> '' AND SUBSTR(js.checkpoint_name, 11) NOT GLOB '*[^0-9]*'
    AND js.checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */
    AND (c.job_id IS NULL OR cr.job_status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */));
