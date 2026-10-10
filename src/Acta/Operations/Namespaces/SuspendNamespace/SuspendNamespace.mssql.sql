SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
DECLARE @id INT, @status TINYINT, @version INT;

BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    SELECT @id = n.id, @status = n.status_code, @version = n.version
    FROM {{schema}}.namespaces n WITH (UPDLOCK, ROWLOCK)
    WHERE n.name = @p_namespace_name;

    IF @id IS NOT NULL AND @status <> 20 /* NamespaceStatusCode.Suspended */
    BEGIN
        UPDATE {{schema}}.namespaces
        SET status_code = 20 /* NamespaceStatusCode.Suspended */, modified_at_utc = @now, version = version + 1
        WHERE id = @id;
        SET @version = @version + 1;

        INSERT INTO {{schema}}.events (event_code, created_at_utc, namespace_id, actor_code, actor, reason_message)
        VALUES (20 /* EventCode.NamespaceSuspended */, @now, @id, @p_actor_code, @p_actor, @p_reason_message);
    END;

    SELECT
        CAST(
            CASE
                WHEN @id IS NULL THEN 2 /* ControlAction.NotFound */
                WHEN @status = 20 /* NamespaceStatusCode.Suspended */ THEN 6 /* ControlAction.AlreadyInState */
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
