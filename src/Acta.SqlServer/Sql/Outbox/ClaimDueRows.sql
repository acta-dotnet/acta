SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    /* Recover expired Claimed leases to Pending, then claim the next due batch and stamp one token + lease
       from the database clock. Both statements run in one source transaction so the claim sees the recovered
       rows. UPDLOCK/READPAST/ROWLOCK lets replicas claim disjoint batches without blocking on each other. */
    UPDATE {{table_ref}}
    SET status_code = 10 /* OutboxStatusCode.Pending */, claim_token = NULL, claim_until_utc = NULL
    WHERE status_code = 20 /* OutboxStatusCode.Claimed */ AND claim_until_utc <= SYSUTCDATETIME();

    /* A laned row waits while an older row of its lane is Pending or Claimed (IOutboxRelayStore.ClaimDueAsync).
       The check carries no READPAST: an older row another relay holds must still count, so it waits out
       that claim or reads its last committed version instead of skipping it. */
    WITH due AS (
        SELECT TOP (@p_batch_size) c.id
        FROM {{table_ref}} AS c WITH (UPDLOCK, READPAST, ROWLOCK)
        WHERE
            c.status_code = 10 /* OutboxStatusCode.Pending */
            AND c.next_attempt_at_utc <= SYSUTCDATETIME()
            AND (
                c.lane IS NULL
                OR NOT EXISTS (
                    SELECT 1
                    FROM {{table_ref}} AS e
                    WHERE
                        e.job_namespace = c.job_namespace
                        AND e.lane = c.lane
                        AND e.id < c.id
                        AND e.status_code IN (10 /* OutboxStatusCode.Pending */, 20 /* OutboxStatusCode.Claimed */)
                )
            )
        ORDER BY c.next_attempt_at_utc ASC, c.id ASC
    )

    UPDATE o
    SET
        status_code = 20 /* OutboxStatusCode.Claimed */,
        claim_token = @p_claim_token,
        claim_until_utc = DATEADD(SECOND, @p_lease_ttl_seconds, SYSUTCDATETIME())
    OUTPUT
        INSERTED.outbox_id, INSERTED.job_namespace, INSERTED.job_name, INSERTED.input_format_id, INSERTED.input,
        INSERTED.deduplication_key, INSERTED.correlation_key, INSERTED.concurrency_key, INSERTED.lane, INSERTED.priority_code,
        INSERTED.next_run_at_utc, INSERTED.delay_seconds, INSERTED.tenant_key, INSERTED.meta, INSERTED.created_at_utc, INSERTED.failure_count,
        INSERTED.id
    FROM {{table_ref}} AS o
    INNER JOIN due ON due.id = o.id;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
