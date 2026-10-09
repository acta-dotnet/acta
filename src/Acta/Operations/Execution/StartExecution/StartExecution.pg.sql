CREATE OR REPLACE FUNCTION {{schema}}.start_execution(
    p_id BIGINT,
    p_leased_by_worker_id INT,
    p_execution_number INT,
    p_lease_ttl_seconds INT
)
RETURNS SMALLINT
LANGUAGE sql
AS $$
    WITH claimed AS (
        /* Locked first, so Started always comes from the row as it is and never from a snapshot. A start that
           committed and lost its answer finds the row Executing under this worker at this execution and is
           answered Started again: the lease is refreshed, the version and the ledger are not touched. */
        SELECT r.job_id, r.status_code AS from_status
        FROM {{schema}}.runtimes r
        WHERE
            r.job_id = p_id
            AND r.leased_by_worker_id = p_leased_by_worker_id
            AND r.execution_number = p_execution_number
            AND r.status_code IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */)
        FOR UPDATE
    ),
    updated AS (
        UPDATE {{schema}}.runtimes r
        SET
            status_code = 50 /* JobStatusCode.Executing */,
            lease_expires_at_utc = now() + (p_lease_ttl_seconds * INTERVAL '1 second'),
            modified_at_utc = now(),
            version = r.version + CASE WHEN c.from_status = 40 /* JobStatusCode.Dispatched */ THEN 1 ELSE 0 END
        FROM claimed c
        INNER JOIN {{schema}}.jobs j ON j.id = c.job_id
        WHERE r.job_id = c.job_id
        RETURNING r.job_id AS id, c.from_status, j.namespace_id, j.lineage_root_id, j.definition_id, j.tenant_id, r.execution_number, j.audit_level_code, j.job_ref
    ),
    event_insert AS (
        INSERT INTO {{schema}}.events (
            event_code,
            created_at_utc,
            namespace_id,
            actor_code,
            actor_key,
            job_id,
            job_ref,
            execution_number,
            lineage_root_id,
            definition_id,
            tenant_id,
            worker_id,
            from_status_code,
            to_status_code,
            execution_status_code,
            duration_ms,
            reason_code,
            reason_message)
        SELECT
            40 /* EventCode.JobExecutionStarted */,
            now(),
            u.namespace_id,
            70 /* ActorCode.Worker */,
            NULL,
            u.id,
            u.job_ref,
            u.execution_number,
            COALESCE(u.lineage_root_id, u.id),
            u.definition_id,
            u.tenant_id,
            p_leased_by_worker_id,
            40 /* JobStatusCode.Dispatched */,
            50 /* JobStatusCode.Executing */,
            50 /* ExecutionStatusCode.Executing */,
            NULL,
            NULL,
            NULL
        FROM updated u
        WHERE u.from_status = 40 /* JobStatusCode.Dispatched */ AND u.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */
        RETURNING 1
    ),
    current AS (
        SELECT r.status_code, r.leased_by_worker_id, r.execution_number
        FROM {{schema}}.runtimes r
        WHERE r.job_id = p_id
    )
    SELECT
        CASE
            WHEN EXISTS (SELECT 1 FROM updated) THEN 1 /* StartExecutionAction.Started */
            WHEN NOT EXISTS (SELECT 1 FROM current) THEN 4 /* StartExecutionAction.AlreadyTerminal */
            WHEN (SELECT status_code FROM current) IN (
                100 /* JobStatusCode.Succeeded */,
                200 /* JobStatusCode.Failed */,
                220 /* JobStatusCode.Cancelled */
            ) THEN 4 /* StartExecutionAction.AlreadyTerminal */
            WHEN (SELECT leased_by_worker_id FROM current) IS DISTINCT FROM p_leased_by_worker_id THEN 2 /* StartExecutionAction.NotOwner */
            ELSE 3 /* StartExecutionAction.LostClaim */
        END::SMALLINT AS action;
$$;
