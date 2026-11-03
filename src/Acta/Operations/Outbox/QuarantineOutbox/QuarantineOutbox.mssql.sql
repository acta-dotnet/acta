SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
DECLARE @lock_resource NVARCHAR(64) = N'acta.outbox:' + CAST(OBJECT_ID(N'{{table_ref}}') AS NVARCHAR(12));
DECLARE @lock_result INT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    -- The table's outbox lock comes first: the row blocks its lane until this commits, so a claim must never skip it
    -- mid-move (docs/sql-internals.md, "Outbox lock").
    EXEC @lock_result = sys.sp_getapplock @lock_resource, 'Exclusive', 'Transaction', -1;
    IF @lock_result < 0
        THROW 51205, 'ACTA:OUTBOX_LOCK:The outbox table lock was not granted (deadlock victim or cancelled).', 1;

    /* Token-CAS set-based quarantine of a claimed group in one round trip: exclude each row from normal claims
       (status 90), clear the claim pair, and record its own consumed failure count and bounded error, so a
       coalesced group can partially quarantine. A row whose token no longer matches changes nothing. */
    -- Seeks by key, so a producer's uncommitted rows never hold it up (docs/sql-internals.md, "Locking reads seek").
    UPDATE o
    SET
        status_code = 90 /* OutboxStatusCode.Quarantined */,
        claim_token = NULL,
        claim_until_utc = NULL,
        failure_count = r.failure_count,
        last_error = r.last_error
    FROM {{table_ref}} AS o WITH (FORCESEEK)
    INNER JOIN
        OPENJSON (@p_rows)
        WITH (
            outbox_id UNIQUEIDENTIFIER '$.outbox_id',
            failure_count INT '$.failure_count',
            last_error NVARCHAR(512) '$.last_error'
        ) AS r
        ON o.outbox_id = r.outbox_id
    WHERE o.claim_token = @p_claim_token AND o.status_code = 20 /* OutboxStatusCode.Claimed */;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
