SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
DECLARE @id INT, @status TINYINT, @version INT;

BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    SELECT @id = t.id, @status = t.status_code, @version = t.version
    FROM {{schema}}.tenants t WITH (UPDLOCK, ROWLOCK)
    WHERE t.tenant_key = @p_tenant_key;

    IF @id IS NOT NULL AND @status <> 20 /* TenantStatusCode.Suspended */
    BEGIN
        UPDATE {{schema}}.tenants
        SET status_code = 20 /* TenantStatusCode.Suspended */, modified_at_utc = @now, version = version + 1
        WHERE id = @id;
        SET @version = @version + 1;

        -- namespace_id 1 is the seeded sys namespace (M001).
        INSERT INTO {{schema}}.events (event_code, created_at_utc, namespace_id, actor_code, actor_key, tenant_id, reason_message)
        VALUES (10 /* EventCode.TenantSuspended */, @now, 1, @p_actor_code, @p_actor_key, @id, @p_reason_message);
    END;

    SELECT
        CAST(
            CASE
                WHEN @id IS NULL THEN 2 /* ControlAction.NotFound */
                WHEN @status = 20 /* TenantStatusCode.Suspended */ THEN 6 /* ControlAction.AlreadyInState */
                ELSE 1 /* ControlAction.Applied */
            END AS SMALLINT
        ) AS action,
        @version AS version;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
