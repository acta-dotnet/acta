/* Recover expired Claimed leases to Pending (immediately eligible), then claim the next due batch and
   stamp one token + lease from the database clock. Both statements run in one source transaction so the
   claim sees the just-recovered rows. FOR UPDATE SKIP LOCKED lets replicas claim disjoint batches. */
UPDATE {{table_ref}}
SET status_code = 10 /* OutboxStatusCode.Pending */, claim_token = NULL, claim_until_utc = NULL
WHERE status_code = 20 /* OutboxStatusCode.Claimed */ AND claim_until_utc <= now();

WITH due AS (
    SELECT outbox_id
    FROM {{table_ref}}
    WHERE status_code = 10 /* OutboxStatusCode.Pending */ AND next_attempt_at_utc <= now()
    ORDER BY COALESCE(priority_code, 50) DESC, next_attempt_at_utc ASC, created_at_utc ASC, outbox_id ASC
    LIMIT @p_batch_size
    FOR UPDATE SKIP LOCKED
)
UPDATE {{table_ref}} o
SET
    status_code = 20 /* OutboxStatusCode.Claimed */,
    claim_token = @p_claim_token,
    claim_until_utc = now() + (@p_lease_ttl_seconds * INTERVAL '1 second')
FROM due
WHERE o.outbox_id = due.outbox_id
RETURNING
    o.outbox_id, o.job_namespace, o.job_name, o.input_format_id, o.input,
    o.deduplication_key, o.correlation_key, o.exclusive_key, o.priority_code,
    o.next_run_at_utc, o.delay_seconds, o.tenant_key, o.meta, o.created_at_utc, o.failure_count;
