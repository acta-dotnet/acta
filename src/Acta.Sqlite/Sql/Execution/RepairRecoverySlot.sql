-- Re-arms one namespace's sys.recovery slot when it is stranded; see IExecutionStore.RepairRecoverySlotAsync.
DROP TABLE IF EXISTS temp._repair_slot;

CREATE TEMP TABLE _repair_slot AS
SELECT r.job_id, r.execution_number, r.status_code AS from_status
FROM {{schema}}.runtimes r
WHERE
    r.job_id = @p_job_id
    AND r.namespace_id = @p_namespace_id
    AND r.status_code IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */)
    AND r.lease_expires_at_utc IS NOT NULL
    AND r.lease_expires_at_utc < {{now}};

INSERT INTO {{schema}}.events (
    event_code,
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
    41 /* EventCode.JobExecutionFinished */,
    j.namespace_id,
    10 /* ActorCode.Sys */,
    NULL,
    j.id,
    j.job_ref,
    s.execution_number,
    COALESCE(j.lineage_root_id, j.id),
    j.definition_id,
    j.tenant_id,
    NULL,
    s.from_status,
    10 /* JobStatusCode.Ready */,
    230 /* ExecutionStatusCode.Orphaned */,
    NULL,
    21 /* JobEventReasonCode.JobLeaseExpired */,
    'Worker lease expired on the recovery slot; re-armed by a worker''s recovery monitor.'
FROM temp._repair_slot s
JOIN {{schema}}.jobs j ON j.id = s.job_id;

UPDATE {{schema}}.runtimes
SET
    status_code = 10 /* JobStatusCode.Ready */,
    next_run_at_utc = {{now}},
    failure_count = MIN(runtimes.failure_count + 1, 32767),
    leased_by_worker_id = NULL,
    lease_expires_at_utc = NULL,
    modified_at_utc = {{now}},
    version = runtimes.version + 1
WHERE job_id IN (SELECT job_id FROM temp._repair_slot);

SELECT CASE
    WHEN EXISTS (SELECT 1 FROM temp._repair_slot) THEN 2 /* RecoverySlotRepair.Repaired */
    WHEN EXISTS (SELECT 1 FROM {{schema}}.runtimes r WHERE r.job_id = @p_job_id AND r.namespace_id = @p_namespace_id) THEN 1 /* RecoverySlotRepair.Healthy */
    ELSE 0 /* RecoverySlotRepair.Missing */ END AS outcome;
