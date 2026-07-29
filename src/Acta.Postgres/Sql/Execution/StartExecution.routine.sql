CREATE OR REPLACE FUNCTION {{schema}}.start_execution(
    p_id                   BIGINT,
    p_leased_by_worker_id  INT,
    p_execution_number     INT,
    p_version              INT,
    p_lease_ttl_seconds    INT
)
RETURNS SMALLINT
LANGUAGE sql
AS $$
    WITH updated AS (
        UPDATE {{schema}}.runtimes r
           SET status_code          = 50 /* JobStatusCode.Executing */,
               lease_expires_at_utc = now() + (p_lease_ttl_seconds * INTERVAL '1 second'),
               modified_at_utc      = now(),
               version              = r.version + 1
          FROM {{schema}}.jobs j
         WHERE r.job_id             = p_id
           AND j.id                 = p_id
           AND r.execution_number   = p_execution_number
           AND r.version            = p_version
           AND r.status_code        = 40 /* JobStatusCode.Dispatched */
           AND r.leased_by_worker_id = p_leased_by_worker_id
           AND r.lease_expires_at_utc > now()
        RETURNING r.job_id AS id, j.namespace_id, j.lineage_root_id, j.definition_id, j.tenant_id, r.execution_number, j.audit_level_code, j.job_ref
    ),

    event_insert AS (
        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id,
            actor_code, actor_key,
            job_id, job_ref, execution_number,
            lineage_root_id, definition_id, tenant_id,
            worker_id,
            from_status_code, to_status_code,
            execution_status_code, duration_ms,
            reason_code, reason_message)
        SELECT
            40 /* JobEventCode.JobExecutionStarted */, now(), u.namespace_id,
            70 /* JobActorCode.Worker */, NULL,
            u.id, u.job_ref, u.execution_number,
            COALESCE(u.lineage_root_id, u.id), u.definition_id, u.tenant_id,
            p_leased_by_worker_id,
            40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */,
            50 /* ExecutionStatusCode.Running */, NULL,
            NULL, NULL
          FROM updated u
         WHERE u.audit_level_code = 20 /* JobAuditLevelCode.Audit */
        RETURNING 1
    ),
    current AS (
        SELECT r.status_code, r.leased_by_worker_id, r.execution_number, r.version, r.lease_expires_at_utc
          FROM {{schema}}.runtimes r
         WHERE r.job_id = p_id
    )
    SELECT
        CASE
            WHEN EXISTS (SELECT 1 FROM updated) THEN 1 /* StartExecutionAction.Started */
            WHEN NOT EXISTS (SELECT 1 FROM current) THEN 4 /* StartExecutionAction.AlreadyTerminal */
            WHEN (SELECT status_code FROM current) IN (
                100 /* JobStatusCode.Done */,
                200 /* JobStatusCode.Failed */,
                220 /* JobStatusCode.Cancelled */
            ) THEN 4 /* StartExecutionAction.AlreadyTerminal */
            WHEN (SELECT leased_by_worker_id FROM current) <> p_leased_by_worker_id
              OR (SELECT leased_by_worker_id FROM current) IS NULL THEN 2 /* StartExecutionAction.NotOwner */
            WHEN (SELECT execution_number FROM current) <> p_execution_number THEN 3 /* StartExecutionAction.LostClaim */
            WHEN (SELECT version FROM current) <> p_version THEN 3 /* StartExecutionAction.LostClaim */
            WHEN (SELECT status_code FROM current) = 40 /* JobStatusCode.Dispatched */
             AND (SELECT lease_expires_at_utc FROM current) <= now() THEN 5 /* StartExecutionAction.LeaseExpired */
            ELSE 4 /* StartExecutionAction.AlreadyTerminal */
        END::SMALLINT AS action;
$$;
