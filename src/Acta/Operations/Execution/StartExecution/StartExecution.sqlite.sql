DROP TABLE IF EXISTS temp._se_claimed;

/* What the row is before the start, captured under SQLite's one writer, so a start that committed and lost its
   answer finds the row Executing under this worker at this execution and is answered Started again: the lease is
   refreshed, the version and the ledger are not touched. */
CREATE TEMP TABLE _se_claimed AS
SELECT r.status_code AS from_status
FROM {{schema}}.runtimes r
WHERE
    r.job_id = @p_id
    AND r.leased_by_worker_id = @p_leased_by_worker_id
    AND r.execution_number = @p_execution_number
    AND r.status_code IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */);

UPDATE {{schema}}.runtimes
SET
    status_code = 50 /* JobStatusCode.Executing */,
    lease_expires_at_utc = {{now}} + (@p_lease_ttl_seconds) * 1000,
    modified_at_utc = {{now}},
    version = version + CASE WHEN status_code = 40 /* JobStatusCode.Dispatched */ THEN 1 ELSE 0 END
WHERE
    job_id = @p_id
    AND leased_by_worker_id = @p_leased_by_worker_id
    AND execution_number = @p_execution_number
    AND status_code IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */);

-- What this call moved, and nothing else.
DROP TABLE IF EXISTS temp._se_done;
CREATE TEMP TABLE _se_done AS SELECT changes() AS moved;

SELECT
    CASE
        WHEN (SELECT moved FROM temp._se_done) = 1 THEN 1 /* StartExecutionAction.Started */
        WHEN NOT EXISTS (
            SELECT 1 FROM {{schema}}.runtimes
            WHERE job_id = @p_id
        ) THEN 4 /* StartExecutionAction.AlreadyTerminal */
        WHEN (
            SELECT status_code FROM {{schema}}.runtimes
            WHERE job_id = @p_id) IN (
            100 /* JobStatusCode.Succeeded */,
            200 /* JobStatusCode.Failed */,
            220 /* JobStatusCode.Cancelled */
        ) THEN 4 /* StartExecutionAction.AlreadyTerminal */
        WHEN
            (
                SELECT leased_by_worker_id FROM {{schema}}.runtimes
                WHERE job_id = @p_id
            ) IS NOT @p_leased_by_worker_id THEN 2 /* StartExecutionAction.NotOwner */
        ELSE 3 /* StartExecutionAction.LostClaim */
    END AS action;

INSERT INTO {{schema}}.events (
    event_code, namespace_id,
    actor_code, actor,
    job_id, job_ref, execution_number,
    lineage_root_id, definition_id, tenant_id,
    worker_id,
    from_status_code, to_status_code,
    execution_status_code, duration_ms,
    reason_code, reason_message
)
SELECT
    40 /* EventCode.JobExecutionStarted */,
    j.namespace_id,
    70 /* ActorCode.Worker */,
    NULL,
    j.id,
    j.job_ref,
    r.execution_number,
    COALESCE(j.lineage_root_id, j.id),
    j.definition_id,
    j.tenant_id,
    @p_leased_by_worker_id,
    40 /* JobStatusCode.Dispatched */,
    50 /* JobStatusCode.Executing */,
    50 /* ExecutionStatusCode.Executing */,
    NULL,
    NULL,
    NULL
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
WHERE
    j.id = @p_id
    AND (SELECT moved FROM temp._se_done) = 1
    AND (SELECT from_status FROM temp._se_claimed) = 40 /* JobStatusCode.Dispatched */
    AND j.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */;
