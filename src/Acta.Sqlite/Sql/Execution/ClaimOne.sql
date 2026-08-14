DROP TABLE IF EXISTS temp._claimed_by_id;

CREATE TEMP TABLE _claimed_by_id AS
SELECT r.job_id AS id
FROM {{schema}}.runtimes r
WHERE
    r.job_id = @p_id
    AND r.namespace_id = @p_namespace_id
    AND r.status_code = 10 /* JobStatusCode.Ready */
    AND (r.next_run_at_utc IS NULL OR r.next_run_at_utc <= {{now}});

UPDATE {{schema}}.runtimes
SET
    status_code = CASE WHEN @p_start_executing = 1 THEN 50 /* JobStatusCode.Executing */ ELSE 40 /* JobStatusCode.Dispatched */ END,
    execution_number = execution_number + 1,
    leased_by_worker_id = @p_leased_by_worker_id,
    lease_expires_at_utc = {{now}} + (@p_lease_ttl_seconds) * 1000,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id IN (SELECT id FROM temp._claimed_by_id)
    AND status_code = 10 /* JobStatusCode.Ready */;

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
    {{now}},
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
    10 /* JobStatusCode.Ready */,
    50 /* JobStatusCode.Executing */,
    50 /* ExecutionStatusCode.Executing */,
    NULL,
    NULL,
    NULL
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
WHERE
    j.id IN (SELECT id FROM temp._claimed_by_id)
    AND @p_start_executing = 1
    AND j.audit_level_code = 20 /* JobAuditLevelCode.Audit */;

SELECT
    j.id,
    j.namespace_id,
    j.definition_id,
    r.execution_number,
    j.deduplication_key,
    j.correlation_key,
    j.exclusive_key,
    j.input_format_id,
    j.input,
    r.next_run_at_utc,
    {{now}} + (@p_lease_ttl_seconds) * 1000 AS lease_expires_at_utc,
    j.created_at_utc,
    r.failure_count,
    r.version,
    j.job_ref,
    j.tenant_id,
    NULL AS db_now,
    NULL AS next_ready_at_utc
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
WHERE j.id IN (SELECT id FROM temp._claimed_by_id);
