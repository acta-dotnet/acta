-- Re-arms one namespace's sys.recovery slot when it is stranded; see IExecutionStore.RepairRecoverySlotAsync.
CREATE OR REPLACE FUNCTION {{schema}}.repair_recovery_slot(
    p_namespace_id INT,
    p_job_id BIGINT
)
RETURNS TABLE (outcome INT)
LANGUAGE sql
AS $$
WITH target AS (
    -- Locked and read before the update so the event can say which in-flight state the slot was in.
    SELECT r.job_id, r.execution_number, r.status_code AS from_status
    FROM {{schema}}.runtimes r
    WHERE
        r.job_id = p_job_id
        AND r.namespace_id = p_namespace_id
        AND r.status_code IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */)
        AND r.lease_expires_at_utc IS NOT NULL
        AND r.lease_expires_at_utc < now()
    FOR UPDATE
),
repaired AS (
    UPDATE {{schema}}.runtimes r
    SET
        status_code = 10 /* JobStatusCode.Ready */,
        next_run_at_utc = now(),
        failure_count = LEAST(r.failure_count + 1, 32767),
        leased_by_worker_id = NULL,
        lease_expires_at_utc = NULL,
        modified_at_utc = now(),
        version = r.version + 1
    FROM target t
    WHERE r.job_id = t.job_id
    RETURNING r.job_id, t.execution_number, t.from_status
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
        41 /* EventCode.JobExecutionFinished */,
        now(),
        j.namespace_id,
        10 /* ActorCode.Sys */,
        NULL,
        j.id,
        j.job_ref,
        x.execution_number,
        COALESCE(j.lineage_root_id, j.id),
        j.definition_id,
        j.tenant_id,
        NULL,
        x.from_status,
        10 /* JobStatusCode.Ready */,
        230 /* ExecutionStatusCode.Orphaned */,
        NULL,
        21 /* JobEventReasonCode.JobLeaseExpired */,
        'Worker lease expired on the recovery slot; re-armed by a worker''s recovery monitor.'
    FROM repaired x
    JOIN {{schema}}.jobs j ON j.id = x.job_id
    RETURNING 1
)
SELECT CASE
    WHEN EXISTS (SELECT 1 FROM repaired) THEN 2 /* RecoverySlotRepair.Repaired */
    WHEN EXISTS (SELECT 1 FROM {{schema}}.runtimes r WHERE r.job_id = p_job_id AND r.namespace_id = p_namespace_id) THEN 1 /* RecoverySlotRepair.Healthy */
    ELSE 0 /* RecoverySlotRepair.Missing */ END AS outcome;
$$;
