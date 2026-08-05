SELECT js.job_id AS parent_job_id,
       CASE WHEN substr(js.name, 11) GLOB '*[^0-9]*' OR substr(js.name, 11) = '' THEN NULL ELSE CAST(substr(js.name, 11) AS INTEGER) END AS child_job_id,
       cr.status_code AS child_status
FROM {{schema}}.checkpoints js
INNER JOIN {{schema}}.jobs p ON p.id = js.job_id AND p.namespace_id = @p_namespace_id
LEFT JOIN {{schema}}.jobs c ON c.id = CASE WHEN substr(js.name, 11) GLOB '*[^0-9]*' OR substr(js.name, 11) = '' THEN NULL ELSE CAST(substr(js.name, 11) AS INTEGER) END
LEFT JOIN {{schema}}.runtimes cr ON cr.job_id = c.id
WHERE js.kind_code = 50 /* JobCheckpointKindCode.ChildLatch */
  AND js.name LIKE 'sys.child.%'
  AND substr(js.name, 11) <> '' AND substr(js.name, 11) NOT GLOB '*[^0-9]*'
  AND js.state_code = 10 /* JobCheckpointStateCode.Pending */
  AND (c.id IS NULL OR cr.status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */));
