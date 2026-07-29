CREATE OR ALTER PROCEDURE {{schema}}.get_stale_child_latches
    @p_namespace_id SMALLINT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT js.job_id AS parent_job_id,
           TRY_CAST(SUBSTRING(js.name, 11, 20) AS BIGINT) AS child_job_id,
           cr.status_code AS child_status
      FROM {{schema}}.checkpoints js
      INNER JOIN {{schema}}.jobs p ON p.id = js.job_id AND p.namespace_id = @p_namespace_id
      LEFT JOIN {{schema}}.jobs c
              ON c.id = TRY_CAST(SUBSTRING(js.name, 11, 20) AS BIGINT)
      LEFT JOIN {{schema}}.runtimes cr ON cr.job_id = c.id
     WHERE js.kind_code = 50 /* JobCheckpointKindCode.ChildLatch */
       AND js.name LIKE 'sys.child.%'
       AND TRY_CAST(SUBSTRING(js.name, 11, 20) AS BIGINT) IS NOT NULL
       AND js.state_code = 10 /* JobCheckpointStateCode.Pending */
       AND (c.id IS NULL OR cr.status_code IN (100 /* JobStatusCode.Done */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */));
END;
GO
