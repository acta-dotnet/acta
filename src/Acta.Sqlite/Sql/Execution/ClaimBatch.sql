DROP TABLE IF EXISTS temp._claimed;
DROP TABLE IF EXISTS temp._claim_bands;

/* Every JobPriorityCode, listed once: the claim seeks each band and the horizon reads each band's head. */
CREATE TEMP TABLE _claim_bands AS
SELECT column1 AS priority_code
FROM (
    VALUES
        (100 /* JobPriorityCode.Realtime */),
        (85 /* JobPriorityCode.Critical */),
        (70 /* JobPriorityCode.High */),
        (50 /* JobPriorityCode.Normal */),
        (0 /* JobPriorityCode.Bulk */)
);

CREATE TEMP TABLE _claimed AS
/* Pure claim-index scan; concurrency-key admission is executor-owned (lock store) after the start CAS,
   and the one jobs lookup is the exclusion below, which an empty set short-circuits. */
SELECT r.job_id AS id, r.status_code AS from_status
FROM {{schema}}.runtimes r
WHERE
    r.namespace_id = @p_namespace_id
    /* One IN key per band: SQLite seeks the bands in ORDER BY order, leaves each at its first row not yet
       due, and never sorts. */
    AND r.priority_code IN (SELECT b.priority_code FROM temp._claim_bands b)
    /* ix_runtimes_claim_ready's filter restated whole: SQLite matches a partial index only against top-level
       AND-terms. Ready always carries its due instant (ck_runtimes_ready_due); a Suspended NULL never comes due. */
    AND r.status_code IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */)
    AND r.next_run_at_utc IS NOT NULL
    AND r.next_run_at_utc <= {{now}}
    /* The definitions this worker already bounced for want of a handler, as JSON array text. NULL on a
       healthy fleet, and the NULL test settles the term before the jobs lookup. */
    AND (
        @p_excluded_definition_ids IS NULL
        OR NOT EXISTS (
            SELECT 1
            FROM {{schema}}.jobs j
            WHERE j.id = r.job_id AND j.definition_id IN (SELECT value FROM json_each(@p_excluded_definition_ids))
        )
    )
ORDER BY
    r.priority_code DESC,
    r.next_run_at_utc ASC,
    r.job_id ASC
LIMIT @p_claim_limit;

UPDATE {{schema}}.runtimes
SET
    status_code = CASE WHEN @p_start_executing = 1 THEN 50 /* JobStatusCode.Executing */ ELSE 40 /* JobStatusCode.Dispatched */ END,
    execution_number = execution_number + 1,
    leased_by_worker_id = @p_leased_by_worker_id,
    lease_expires_at_utc = {{now}} + (@p_lease_ttl_seconds) * 1000,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id IN (SELECT id FROM temp._claimed)
    AND status_code IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */);

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
    c.from_status,
    50 /* JobStatusCode.Executing */,
    50 /* ExecutionStatusCode.Executing */,
    NULL,
    NULL,
    NULL
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
JOIN temp._claimed c ON c.id = j.id
WHERE
    @p_start_executing = 1
    AND j.audit_level_code = 20 /* JobAuditLevelCode.Audit */;

SELECT
    j.id,
    j.namespace_id,
    j.definition_id,
    r.execution_number,
    j.deduplication_key,
    j.correlation_key,
    j.concurrency_key,
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
WHERE j.id IN (SELECT id FROM temp._claimed)
UNION ALL
SELECT
    NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
    {{now}},
    /* The earliest head across the bands: each band's first index row is its earliest instant. */
    (
        SELECT
            MIN((
                SELECT r.next_run_at_utc
                FROM {{schema}}.runtimes r
                WHERE
                    r.namespace_id = @p_namespace_id
                    AND r.priority_code = b.priority_code
                    AND r.status_code IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */)
                    AND r.next_run_at_utc IS NOT NULL
                    /* Excluded rows are invisible to this worker's horizon too: a horizon at or before now is
                       what tells the caller to retry at the anti-spin floor, so counting rows this worker will
                       never claim would spin it. */
                    AND (
                        @p_excluded_definition_ids IS NULL
                        OR NOT EXISTS (
                            SELECT 1
                            FROM {{schema}}.jobs j
                            WHERE j.id = r.job_id AND j.definition_id IN (SELECT value FROM json_each(@p_excluded_definition_ids))
                        )
                    )
                ORDER BY r.next_run_at_utc
                LIMIT 1
            ))
        FROM temp._claim_bands b
    )
WHERE NOT EXISTS (SELECT 1 FROM temp._claimed)
ORDER BY id;
