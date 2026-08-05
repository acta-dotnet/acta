UPDATE {{schema}}.runtimes
   SET status_code          = 50 /* JobStatusCode.Executing */,
       lease_expires_at_utc = {{now}} + (@p_lease_ttl_seconds) * 1000,
       modified_at_utc      = {{now}},
       version              = version + 1
 WHERE job_id              = @p_id
   AND execution_number    = @p_execution_number
   AND version             = @p_version
   AND status_code         = 40 /* JobStatusCode.Dispatched */
   AND leased_by_worker_id = @p_leased_by_worker_id
   AND lease_expires_at_utc > {{now}};

SELECT
    CASE
        WHEN EXISTS (SELECT 1 FROM {{schema}}.runtimes r
                      WHERE r.job_id = @p_id
                        AND r.status_code = 50 /* JobStatusCode.Executing */
                        AND r.execution_number = @p_execution_number
                        AND r.version = @p_version + 1) THEN 1 /* StartExecutionAction.Started */
        WHEN NOT EXISTS (SELECT 1 FROM {{schema}}.runtimes WHERE job_id = @p_id) THEN 4 /* StartExecutionAction.AlreadyTerminal */
        WHEN (SELECT status_code FROM {{schema}}.runtimes WHERE job_id = @p_id) IN (
            100 /* JobStatusCode.Succeeded */,
            200 /* JobStatusCode.Failed */,
            220 /* JobStatusCode.Cancelled */
        ) THEN 4 /* StartExecutionAction.AlreadyTerminal */
        WHEN (SELECT leased_by_worker_id FROM {{schema}}.runtimes WHERE job_id = @p_id) <> @p_leased_by_worker_id
          OR (SELECT leased_by_worker_id FROM {{schema}}.runtimes WHERE job_id = @p_id) IS NULL THEN 2 /* StartExecutionAction.NotOwner */
        WHEN (SELECT execution_number FROM {{schema}}.runtimes WHERE job_id = @p_id) <> @p_execution_number THEN 3 /* StartExecutionAction.LostClaim */
        WHEN (SELECT version FROM {{schema}}.runtimes WHERE job_id = @p_id) <> @p_version THEN 3 /* StartExecutionAction.LostClaim */
        WHEN (SELECT status_code FROM {{schema}}.runtimes WHERE job_id = @p_id) = 40 /* JobStatusCode.Dispatched */
         AND (SELECT lease_expires_at_utc FROM {{schema}}.runtimes WHERE job_id = @p_id) <= {{now}} THEN 5 /* StartExecutionAction.LeaseExpired */
        ELSE 4 /* StartExecutionAction.AlreadyTerminal */
    END AS action;

INSERT INTO {{schema}}.events (
    event_code, namespace_id,
    actor_code, actor_key,
    job_id, job_ref, execution_number,
    lineage_root_id, definition_id, tenant_id,
    worker_id,
    from_status_code, to_status_code,
    execution_status_code, duration_ms,
    reason_code, reason_message)
SELECT
    40 /* JobEventCode.JobExecutionStarted */, j.namespace_id,
    70 /* JobActorCode.Worker */, NULL,
    j.id, j.job_ref, r.execution_number,
    COALESCE(j.lineage_root_id, j.id), j.definition_id, j.tenant_id,
    @p_leased_by_worker_id,
    40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */,
    50 /* ExecutionStatusCode.Executing */, NULL,
    NULL, NULL
  FROM {{schema}}.jobs j
  JOIN {{schema}}.runtimes r ON r.job_id = j.id
 WHERE j.id = @p_id
   AND r.status_code = 50 /* JobStatusCode.Executing */
   AND r.execution_number = @p_execution_number
   AND r.version = @p_version + 1
   AND j.audit_level_code = 20 /* JobAuditLevelCode.Audit */;
