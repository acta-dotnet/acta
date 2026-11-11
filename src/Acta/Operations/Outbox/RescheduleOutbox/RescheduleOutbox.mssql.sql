SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
DECLARE @lock_resource NVARCHAR(64) = N'acta.outbox:' + CAST(OBJECT_ID(N'{{table_ref}}') AS NVARCHAR(12));
DECLARE @lock_result INT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    -- The table's outbox lock comes first: the row stays its lane's blocker, so a claim must never skip it mid-move
    -- (docs/sql-internals.md, "Outbox lock").
    EXEC @lock_result = sys.sp_getapplock @lock_resource, 'Exclusive', 'Transaction', -1;
    IF @lock_result < 0
        THROW 51205, 'ACTA:OUTBOX_LOCK:The outbox table lock was not granted (deadlock victim or cancelled).', 1;

    /* Token-CAS set-based reschedule of a recoverable claimed group: each row returns to Pending with its own
       bumped failure count, per-row backoff added to the source clock (SYSUTCDATETIME), and bounded error; the
       claim pair clears. A row whose token no longer matches changes nothing. */
    -- Seeks by key, so a producer's uncommitted rows never hold it up (docs/sql-internals.md, "Locking reads seek").
    UPDATE o
    SET
        outbox_status_code = 10 /* OutboxStatusCode.Pending */,
        claim_token = NULL,
        claim_until_utc = NULL,
        failure_count = r.failure_count,
        next_attempt_at_utc = DATEADD(SECOND, r.backoff_seconds, SYSUTCDATETIME()),
        failure_message = r.failure_message
    FROM {{table_ref}} AS o WITH (FORCESEEK)
    INNER JOIN
        OPENJSON (@p_rows)
        WITH (
            outbox_id UNIQUEIDENTIFIER '$.outbox_id',
            failure_count INT '$.failure_count',
            backoff_seconds INT '$.backoff_seconds',
            failure_message NVARCHAR(512) '$.failure_message'
        ) AS r
        ON o.outbox_id = r.outbox_id
    WHERE o.claim_token = @p_claim_token AND o.outbox_status_code = 20 /* OutboxStatusCode.Claimed */;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
