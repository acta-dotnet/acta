DROP TABLE IF EXISTS temp._ces_in;
DROP TABLE IF EXISTS temp._ces_done;

-- The completions arrive as JSON array text, each result as base64. acta_blob answers NULL for no bytes, and an empty
-- result is stored as a zero-length blob.
CREATE TEMP TABLE _ces_in AS
SELECT
    json_extract(c.value, '$.job_id') AS job_id,
    json_extract(c.value, '$.execution_number') AS execution_number,
    json_extract(c.value, '$.succeeded') AS succeeded,
    json_extract(c.value, '$.result_format_id') AS result_format_id,
    COALESCE(acta_blob(json_extract(c.value, '$.result')), X'') AS result,
    json_extract(c.value, '$.reason_code') AS reason_code,
    json_extract(c.value, '$.reason_message') AS reason_message,
    json_extract(c.value, '$.duration_ms') AS duration_ms,
    json_extract(c.value, '$.failure_count') AS failure_count,
    json_extract(c.value, '$.retention_seconds') AS retention_seconds
FROM json_each(@p_completions) c;

-- A job with a parent or a lane is answered PerJob and left untouched.
CREATE TEMP TABLE _ces_done AS
SELECT
    c.*,
    j.namespace_id,
    j.lineage_root_id,
    j.definition_id,
    j.tenant_id,
    j.audit_level_code,
    j.job_ref
FROM temp._ces_in c
INNER JOIN {{schema}}.runtimes r ON r.job_id = c.job_id
INNER JOIN {{schema}}.jobs j ON j.id = c.job_id
WHERE
    r.execution_number = c.execution_number
    AND r.status_code = 50 /* JobStatusCode.Executing */
    AND r.leased_by_worker_id = @p_leased_by_worker_id
    AND r.lane_id IS NULL
    AND j.parent_id IS NULL;

UPDATE {{schema}}.runtimes AS r
SET
    status_code = CASE WHEN d.succeeded = 1 THEN 100 /* JobStatusCode.Succeeded */ ELSE 200 /* JobStatusCode.Failed */ END,
    failure_count = COALESCE(d.failure_count, r.failure_count),
    leased_by_worker_id = NULL,
    lease_expires_at_utc = NULL,
    retention_until_utc = COALESCE({{now}} + d.retention_seconds * 1000, r.retention_until_utc),
    modified_at_utc = {{now}},
    version = r.version + 1
FROM temp._ces_done d
WHERE r.job_id = d.job_id;

INSERT INTO {{schema}}.results (job_id, execution_number, result_format_id, result, created_at_utc)
SELECT d.job_id, d.execution_number, d.result_format_id, d.result, {{now}}
FROM temp._ces_done d
WHERE d.result_format_id <> 0 /* JobPayloadFormat.None */;

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
    {{now}},
    d.namespace_id,
    70 /* ActorCode.Worker */,
    NULL,
    d.job_id,
    d.job_ref,
    d.execution_number,
    COALESCE(d.lineage_root_id, d.job_id),
    d.definition_id,
    d.tenant_id,
    @p_leased_by_worker_id,
    50 /* JobStatusCode.Executing */,
    CASE WHEN d.succeeded = 1 THEN 100 /* JobStatusCode.Succeeded */ ELSE 200 /* JobStatusCode.Failed */ END,
    CASE WHEN d.succeeded = 1 THEN 100 /* ExecutionStatusCode.Succeeded */ ELSE 200 /* ExecutionStatusCode.Failed */ END,
    d.duration_ms,
    d.reason_code,
    d.reason_message
FROM temp._ces_done d
-- At Failures a failure is written, and a success only when it answers a recorded failure: the job's newest finished
-- event is not a success. One seek on the timeline index per row.
WHERE
    d.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */
    OR (
        d.audit_level_code = 10 /* DefinitionAuditLevelCode.Failures */
        AND (
            d.succeeded = 0
            OR COALESCE((
                SELECT e.execution_status_code
                FROM {{schema}}.events e
                WHERE e.job_id = d.job_id AND e.event_code = 41 /* EventCode.JobExecutionFinished */
                ORDER BY e.created_at_utc DESC, e.id DESC
                LIMIT 1), 100) <> 100 /* ExecutionStatusCode.Succeeded */
        )
    );

SELECT
    c.job_id,
    c.execution_number,
    CASE
        WHEN d.job_id IS NOT NULL THEN 1 /* CompleteExecutionsAction.Completed */
        WHEN
            r.status_code IS NULL
            OR r.status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
            THEN 3 /* CompleteExecutionsAction.AlreadyTerminal */
        WHEN r.leased_by_worker_id IS NULL OR r.leased_by_worker_id <> @p_leased_by_worker_id THEN 2 /* CompleteExecutionsAction.NotOwner */
        WHEN r.lane_id IS NOT NULL OR j.parent_id IS NOT NULL THEN 4 /* CompleteExecutionsAction.PerJob */
        ELSE 3 /* CompleteExecutionsAction.AlreadyTerminal */
    END AS action,
    r.status_code AS final_status_code,
    {{now}} AS db_now
FROM temp._ces_in c
LEFT JOIN temp._ces_done d ON d.job_id = c.job_id AND d.execution_number = c.execution_number
LEFT JOIN {{schema}}.runtimes r ON r.job_id = c.job_id
LEFT JOIN {{schema}}.jobs j ON j.id = c.job_id;
