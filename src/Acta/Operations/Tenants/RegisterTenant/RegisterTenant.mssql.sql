SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
DECLARE @tenant_id INT;

BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    -- Hold the indexed key (or gap) until the operation commits; no unique-error recovery in an aborted transaction.
    SELECT @tenant_id = tenant_id
    FROM {{schema}}.tenants WITH (UPDLOCK, HOLDLOCK)
    WHERE tenant_name = @p_tenant_name;

    IF @tenant_id IS NULL
    BEGIN
        INSERT INTO {{schema}}.tenants
            (tenant_name, display_name, description, tenant_status_code, created_at_utc, modified_at_utc, version)
        VALUES (@p_tenant_name, @p_display_name, @p_description, 10 /* TenantStatusCode.Active */, @now, @now, 0);
        SET @tenant_id = CAST(SCOPE_IDENTITY() AS INT);
    END;

    SELECT @tenant_id AS tenant_id, @p_tenant_name AS tenant_name;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
