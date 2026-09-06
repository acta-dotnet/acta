CREATE OR ALTER PROCEDURE {{schema}}.suspend_tenant
    @p_tenant_key VARCHAR(128),
    @p_actor_code TINYINT,
    @p_actor_key NVARCHAR(128),
    @p_reason_message NVARCHAR(512)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
        DECLARE @id INT, @status TINYINT, @version INT;

        SELECT
            @id = t.id,
            @status = t.status_code,
            @version = t.version
        FROM {{schema}}.tenants t WITH (UPDLOCK, ROWLOCK)
        WHERE t.tenant_key = @p_tenant_key;

        IF @id IS NULL
            BEGIN

                SELECT
                    CAST(2 /* AdminControlAction.NotFound */ AS SMALLINT) AS action,
                    CAST(NULL AS INT) AS version;
                GOTO Finish;
            END;

        IF @status = 20 /* TenantStatusCode.Suspended */
            BEGIN

                SELECT
                    CAST(3 /* AdminControlAction.AlreadyInState */ AS SMALLINT) AS action,
                    @version AS version;
                GOTO Finish;
            END;

        UPDATE {{schema}}.tenants
        SET status_code = 20 /* TenantStatusCode.Suspended */, modified_at_utc = @now, version = version + 1
        WHERE id = @id;
        SET @version = @version + 1;

        -- namespace_id 1 is the seeded sys namespace (M001).
        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id, actor_code, actor_key,
            job_id, job_ref, execution_number, lineage_root_id, definition_id, tenant_id, worker_id,
            from_status_code, to_status_code, execution_status_code, duration_ms, reason_code, reason_message
        )
        VALUES (
            10 /* EventCode.TenantSuspended */, @now, 1, @p_actor_code, @p_actor_key,
            NULL, NULL, NULL, NULL, NULL, @id, NULL,
            NULL, NULL, NULL, NULL, NULL, @p_reason_message
        );

        SELECT
            CAST(1 /* AdminControlAction.Applied */ AS SMALLINT) AS action,
            @version AS version;

    Finish:

        IF @entry_trancount = 0
            COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @entry_trancount = 0 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO
