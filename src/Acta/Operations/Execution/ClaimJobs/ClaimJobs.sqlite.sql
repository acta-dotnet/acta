DROP TABLE IF EXISTS temp._claimed;
DROP TABLE IF EXISTS temp._claim_bands;

/* Every JobPriorityCode, listed once: the claim seeks each band and the horizon reads each band's head. */
CREATE TEMP TABLE _claim_bands AS
SELECT column1 AS job_priority_code
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
SELECT r.job_id
FROM {{schema}}.jobs_state r
WHERE
    r.namespace_id = @p_namespace_id
    /* One IN key per band: SQLite seeks the bands in ORDER BY order, leaves each at its first row not yet
       due, and never sorts. */
    AND r.job_priority_code IN (SELECT b.job_priority_code FROM temp._claim_bands b)
    /* ix_jobs_state_claim_ready's filter restated whole: SQLite matches a partial index only against top-level
       AND-terms. Ready always carries its due instant (ck_jobs_state_ready_due); a Suspended NULL never comes due. */
    AND r.job_status_code IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */)
    AND r.next_run_at_utc IS NOT NULL
    AND r.next_run_at_utc <= {{now}}
    /* The worker's watermark, as on the server providers (docs/sql-internals.md, "Claim
       watermark"); SQLite frees a deleted index entry at once, so it only keeps the claim alike here.
       Without one the scan starts at DateTime.MinValue in epoch milliseconds, so a row due before 1970 is reached. */
    AND r.next_run_at_utc >= COALESCE(@p_claim_from_utc, -62135596800000)
    /* The definitions this worker already bounced for want of a handler, as JSON array text. NULL on a
       healthy fleet, and the NULL test settles the term before the jobs lookup. */
    AND (
        @p_excluded_definition_ids IS NULL
        OR NOT EXISTS (
            SELECT 1
            FROM {{schema}}.jobs j
            WHERE j.job_id = r.job_id AND j.definition_id IN (SELECT value FROM json_each(@p_excluded_definition_ids))
        )
    )
ORDER BY
    r.job_priority_code DESC,
    r.next_run_at_utc ASC,
    r.job_id ASC
LIMIT @p_claim_limit;

UPDATE {{schema}}.jobs_state
SET
    job_status_code = 50 /* JobStatusCode.Executing */,
    execution_number = execution_number + 1,
    leased_by_worker_id = @p_leased_by_worker_id,
    lease_expires_at_utc = {{now}} + (@p_lease_ttl_seconds) * 1000,
    result_format_id = 0 /* JobPayloadFormat.None */,
    result = NULL,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id IN (SELECT job_id FROM temp._claimed)
    AND job_status_code IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */);

SELECT
    j.job_id,
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
    NULL AS db_now,
    NULL AS next_ready_at_utc,
    r.lane_id,
    j.parent_job_id
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
WHERE j.job_id IN (SELECT job_id FROM temp._claimed)
UNION ALL
SELECT
    NULL AS job_id,
    NULL AS definition_id,
    NULL AS execution_number,
    NULL AS deduplication_key,
    NULL AS correlation_key,
    NULL AS concurrency_key,
    NULL AS input_format_id,
    NULL AS input,
    NULL AS next_run_at_utc,
    NULL AS created_at_utc,
    NULL AS failure_count,
    NULL AS job_ref,
    NULL AS tenant_id,
    {{now}} AS db_now,
    /* The earliest head across the bands: each band's first index row is its earliest instant. */
    (
        SELECT
            MIN((
                SELECT r.next_run_at_utc
                FROM {{schema}}.jobs_state r
                WHERE
                    r.namespace_id = @p_namespace_id
                    AND r.job_priority_code = b.job_priority_code
                    AND r.job_status_code IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */)
                    AND r.next_run_at_utc IS NOT NULL
                    /* Excluded rows are invisible to this worker's horizon too: a horizon at or before now is
                       what tells the caller to retry at the anti-spin floor, so counting rows this worker will
                       never claim would spin it. */
                    AND (
                        @p_excluded_definition_ids IS NULL
                        OR NOT EXISTS (
                            SELECT 1
                            FROM {{schema}}.jobs j
                            WHERE j.job_id = r.job_id AND j.definition_id IN (SELECT value FROM json_each(@p_excluded_definition_ids))
                        )
                    )
                ORDER BY r.next_run_at_utc
                LIMIT 1
            ))
        FROM temp._claim_bands b
    ) AS next_ready_at_utc,
    NULL AS lane_id,
    NULL AS parent_job_id
WHERE NOT EXISTS (SELECT 1 FROM temp._claimed)
ORDER BY job_id;
