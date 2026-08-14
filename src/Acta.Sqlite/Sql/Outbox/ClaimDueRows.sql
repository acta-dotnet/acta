/* Recover expired Claimed leases to Pending, then claim the next due batch and stamp one token + lease,
   under one BEGIN IMMEDIATE write lock. Instants are the canonical outbox SQLite ISO text, so the clock and
   lease use strftime, not the ledger's epoch-milliseconds encoding. */
UPDATE {{table_ref}}
SET status_code = 10 /* OutboxStatusCode.Pending */, claim_token = NULL, claim_until_utc = NULL
WHERE status_code = 20 /* OutboxStatusCode.Claimed */ AND claim_until_utc <= STRFTIME('%Y-%m-%d %H:%M:%f', 'now');

UPDATE {{table_ref}}
SET
    status_code = 20 /* OutboxStatusCode.Claimed */,
    claim_token = @p_claim_token,
    claim_until_utc = STRFTIME('%Y-%m-%d %H:%M:%f', 'now', '+' || CAST(@p_lease_ttl_seconds AS TEXT) || ' seconds')
WHERE
    outbox_id IN (
        SELECT outbox_id
        FROM {{table_ref}}
        WHERE status_code = 10 /* OutboxStatusCode.Pending */ AND next_attempt_at_utc <= STRFTIME('%Y-%m-%d %H:%M:%f', 'now')
        ORDER BY COALESCE(priority_code, 50) DESC, next_attempt_at_utc ASC, created_at_utc ASC, outbox_id ASC
        LIMIT @p_batch_size
    )
RETURNING
    outbox_id, job_namespace, job_name, input_format_id, input,
    deduplication_key, correlation_key, exclusive_key, priority_code,
    next_run_at_utc, delay_seconds, tenant_key, meta, created_at_utc, failure_count;
