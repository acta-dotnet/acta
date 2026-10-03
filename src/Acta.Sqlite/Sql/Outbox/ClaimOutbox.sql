/* Recover expired Claimed leases to Pending, then claim the next due batch and stamp one token + lease,
   under one BEGIN IMMEDIATE write lock. Instants are the canonical outbox SQLite ISO text, so the clock and
   lease use strftime, not the ledger's epoch-milliseconds encoding. */
UPDATE {{table_ref}}
SET status_code = 10 /* OutboxStatusCode.Pending */, claim_token = NULL, claim_until_utc = NULL
WHERE status_code = 20 /* OutboxStatusCode.Claimed */ AND claim_until_utc <= STRFTIME('%Y-%m-%d %H:%M:%f', 'now');

/* A laned row waits while an older row of its lane is Pending or Claimed (IOutboxRelayStore.ClaimDueAsync). */
UPDATE {{table_ref}}
SET
    status_code = 20 /* OutboxStatusCode.Claimed */,
    claim_token = @p_claim_token,
    claim_until_utc = STRFTIME('%Y-%m-%d %H:%M:%f', 'now', '+' || CAST(@p_lease_ttl_seconds AS TEXT) || ' seconds')
WHERE
    id IN (
        SELECT c.id
        FROM {{table_ref}} c
        WHERE
            c.status_code = 10 /* OutboxStatusCode.Pending */
            AND c.next_attempt_at_utc <= STRFTIME('%Y-%m-%d %H:%M:%f', 'now')
            AND (
                c.lane IS NULL
                OR NOT EXISTS (
                    SELECT 1
                    FROM {{table_ref}} e
                    WHERE
                        e.job_namespace = c.job_namespace
                        AND e.lane = c.lane
                        AND e.id < c.id
                        AND e.status_code IN (10 /* OutboxStatusCode.Pending */, 20 /* OutboxStatusCode.Claimed */)
                )
            )
        ORDER BY c.next_attempt_at_utc ASC, c.id ASC
        LIMIT @p_batch_size
    )
RETURNING
    outbox_id, job_namespace, job_name, input_format_id, input,
    deduplication_key, correlation_key, concurrency_key, lane, priority_code,
    next_run_at_utc, delay_seconds, tenant_key, meta, created_at_utc, failure_count, id;
