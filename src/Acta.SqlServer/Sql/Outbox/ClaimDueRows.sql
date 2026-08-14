/* Recover expired Claimed leases to Pending, then claim the next due batch and stamp one token + lease
   from the database clock. Both statements run in one source transaction so the claim sees the recovered
   rows. UPDLOCK/READPAST/ROWLOCK lets replicas claim disjoint batches without blocking on each other. */
UPDATE {{table_ref}}
SET status_code = 10 /* OutboxStatusCode.Pending */, claim_token = NULL, claim_until_utc = NULL
WHERE status_code = 20 /* OutboxStatusCode.Claimed */ AND claim_until_utc <= SYSUTCDATETIME();

WITH due AS (
    SELECT TOP (@p_batch_size) outbox_id
    FROM {{table_ref}} WITH (UPDLOCK, READPAST, ROWLOCK)
    WHERE status_code = 10 /* OutboxStatusCode.Pending */ AND next_attempt_at_utc <= SYSUTCDATETIME()
    ORDER BY COALESCE(priority_code, 50) DESC, next_attempt_at_utc ASC, created_at_utc ASC, outbox_id ASC
)

UPDATE o
SET
    status_code = 20 /* OutboxStatusCode.Claimed */,
    claim_token = @p_claim_token,
    claim_until_utc = DATEADD(SECOND, @p_lease_ttl_seconds, SYSUTCDATETIME())
OUTPUT
    INSERTED.outbox_id, INSERTED.job_namespace, INSERTED.job_name, INSERTED.input_format_id, INSERTED.input,
    INSERTED.deduplication_key, INSERTED.correlation_key, INSERTED.exclusive_key, INSERTED.priority_code,
    INSERTED.next_run_at_utc, INSERTED.delay_seconds, INSERTED.tenant_key, INSERTED.meta, INSERTED.created_at_utc, INSERTED.failure_count
FROM {{table_ref}} AS o
INNER JOIN due ON due.outbox_id = o.outbox_id;
