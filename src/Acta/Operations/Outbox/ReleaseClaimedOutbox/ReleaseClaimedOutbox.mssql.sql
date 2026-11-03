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

    /* Token-CAS set-based release of an unprocessed claimed group back to Pending, clearing the claim pair
       and leaving next_attempt_at_utc unchanged. Best-effort: lease expiry and token-CAS keep it safe. */
    -- Seeks by key, so a producer's uncommitted rows never hold it up (docs/sql-internals.md, "Locking reads seek").
    UPDATE o
    SET
        status_code = 10 /* OutboxStatusCode.Pending */,
        claim_token = NULL,
        claim_until_utc = NULL
    FROM {{table_ref}} AS o WITH (FORCESEEK)
    WHERE
        o.claim_token = @p_claim_token
        AND o.status_code = 20 /* OutboxStatusCode.Claimed */
        AND o.outbox_id IN (SELECT CAST(value AS UNIQUEIDENTIFIER) FROM OPENJSON(@p_outbox_ids));

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
