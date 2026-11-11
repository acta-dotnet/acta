SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
DECLARE @lock_resource NVARCHAR(64) = N'acta.outbox:' + CAST(OBJECT_ID(N'{{table_ref}}') AS NVARCHAR(12));
DECLARE @lock_result INT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    -- The claim, reschedule, release, and quarantine take the table's outbox lock first, so none of them moves a
    -- lane's head while the lane check below skips held rows (docs/sql-internals.md, "Outbox lock").
    EXEC @lock_result = sys.sp_getapplock @lock_resource, 'Exclusive', 'Transaction', -1;
    IF @lock_result < 0
        THROW 51205, 'ACTA:OUTBOX_LOCK:The outbox table lock was not granted (deadlock victim or cancelled).', 1;

    /* Recover expired Claimed leases to Pending, then claim the next due batch and stamp one token + lease
       from the database clock. Both statements run in one source transaction so the claim sees the recovered
       rows. READPAST passes over a row a producer has not committed yet. */
    -- Every read seeks and locks rows only (docs/sql-internals.md, "Locking reads seek"), with no index named:
    -- the producer owns this table's DDL and may rename its indexes.
    UPDATE o
    SET outbox_status_code = 10 /* OutboxStatusCode.Pending */, claim_token = NULL, claim_until_utc = NULL
    FROM {{table_ref}} AS o WITH (FORCESEEK, ROWLOCK)
    WHERE o.outbox_status_code = 20 /* OutboxStatusCode.Claimed */ AND o.claim_until_utc <= SYSUTCDATETIME();

    /* A laned row waits while an older row of its lane is Pending or Claimed (OutboxOperations.Source.ClaimAsync).
       The check skips a row a producer has not committed, which counts as absent, as under read-committed
       snapshot. */
    WITH due AS (
        SELECT TOP (@p_batch_size) c.id
        FROM {{table_ref}} AS c WITH (UPDLOCK, READPAST, ROWLOCK, FORCESEEK)
        WHERE
            c.outbox_status_code = 10 /* OutboxStatusCode.Pending */
            AND c.next_attempt_at_utc <= SYSUTCDATETIME()
            AND (
                c.lane_name IS NULL
                OR NOT EXISTS (
                    SELECT 1
                    FROM {{table_ref}} AS e WITH (READCOMMITTEDLOCK, READPAST, FORCESEEK)
                    WHERE
                        e.namespace_name = c.namespace_name
                        AND e.lane_name = c.lane_name
                        AND e.id < c.id
                        AND e.outbox_status_code IN (10 /* OutboxStatusCode.Pending */, 20 /* OutboxStatusCode.Claimed */)
                )
            )
        ORDER BY c.next_attempt_at_utc ASC, c.id ASC
    )

    UPDATE o
    SET
        outbox_status_code = 20 /* OutboxStatusCode.Claimed */,
        claim_token = @p_claim_token,
        claim_until_utc = DATEADD(SECOND, @p_lease_ttl_seconds, SYSUTCDATETIME())
    OUTPUT
        INSERTED.outbox_id, INSERTED.namespace_name, INSERTED.job_name, INSERTED.input_format_id, INSERTED.input,
        INSERTED.deduplication_key, INSERTED.correlation_key, INSERTED.concurrency_key, INSERTED.lane_name, INSERTED.job_priority_code,
        INSERTED.next_run_at_utc, INSERTED.delay_seconds, INSERTED.tenant_name, INSERTED.meta, INSERTED.failure_count, INSERTED.id
    FROM {{table_ref}} AS o WITH (FORCESEEK, ROWLOCK)
    INNER JOIN due ON due.id = o.id;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
