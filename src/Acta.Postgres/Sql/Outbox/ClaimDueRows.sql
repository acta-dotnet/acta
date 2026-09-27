/* Recover expired Claimed leases to Pending (immediately eligible), then claim the next due batch and
   stamp one token + lease from the database clock. Both statements run in one source transaction so the
   claim sees the just-recovered rows. FOR UPDATE SKIP LOCKED lets replicas claim disjoint batches. */
UPDATE {{table_ref}}
SET status_code = 10 /* OutboxStatusCode.Pending */, claim_token = NULL, claim_until_utc = NULL
WHERE status_code = 20 /* OutboxStatusCode.Claimed */ AND claim_until_utc <= now();

/* A laned row waits while an older row of its lane is Pending or Claimed (IOutboxRelayStore.ClaimDueAsync).
   SKIP LOCKED applies only to the rows FOR UPDATE returns, so an older row another relay holds still counts. */
WITH due AS (
    SELECT c.id
    FROM {{table_ref}} c
    WHERE
        c.status_code = 10 /* OutboxStatusCode.Pending */
        AND c.next_attempt_at_utc <= now()
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
    FOR UPDATE OF c SKIP LOCKED
)
UPDATE {{table_ref}} o
SET
    status_code = 20 /* OutboxStatusCode.Claimed */,
    claim_token = @p_claim_token,
    claim_until_utc = now() + (@p_lease_ttl_seconds * INTERVAL '1 second')
FROM due
WHERE o.id = due.id
RETURNING
    o.outbox_id, o.job_namespace, o.job_name, o.input_format_id, o.input,
    o.deduplication_key, o.correlation_key, o.concurrency_key, o.lane, o.priority_code,
    o.next_run_at_utc, o.delay_seconds, o.tenant_key, o.meta, o.created_at_utc, o.failure_count, o.id;
