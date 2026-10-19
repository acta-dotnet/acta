DROP TABLE IF EXISTS temp._claimed_by_id;

/* ClaimJobs' due test: a Ready row always carries its due instant (ck_runtimes_ready_due), and a
   Suspended row with a NULL instant is an unbounded wait that never comes due. */
CREATE TEMP TABLE _claimed_by_id AS
SELECT r.job_id AS id
FROM {{schema}}.runtimes r
WHERE
    r.job_id = @p_id
    AND r.namespace_id = @p_namespace_id
    AND r.status_code IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */)
    AND r.next_run_at_utc IS NOT NULL
    AND r.next_run_at_utc <= {{now}};

UPDATE {{schema}}.runtimes
SET
    status_code = 50 /* JobStatusCode.Executing */,
    execution_number = execution_number + 1,
    leased_by_worker_id = @p_leased_by_worker_id,
    lease_expires_at_utc = {{now}} + (@p_lease_ttl_seconds) * 1000,
    result_format_id = 0 /* JobPayloadFormat.None */,
    result = NULL,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id IN (SELECT id FROM temp._claimed_by_id)
    AND status_code IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */);

SELECT
    j.id,
    j.definition_id,
    r.execution_number,
    j.deduplication_key,
    j.correlation_key,
    j.concurrency_key,
    j.input_format_id,
    j.input,
    r.next_run_at_utc,
    j.created_at_utc,
    r.failure_count,
    j.job_ref,
    j.tenant_id,
    r.lane_id,
    j.parent_id
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
WHERE j.id IN (SELECT id FROM temp._claimed_by_id);
