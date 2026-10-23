SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
DECLARE @namespace_id INT, @status TINYINT, @version INT;

BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    SELECT @namespace_id = n.namespace_id, @status = n.namespace_status_code, @version = n.version
    FROM {{schema}}.namespaces n WITH (UPDLOCK, ROWLOCK)
    WHERE n.namespace_name = @p_namespace_name;

    IF @namespace_id IS NOT NULL AND @status <> 20 /* NamespaceStatusCode.Suspended */
    BEGIN
        UPDATE {{schema}}.namespaces
        SET namespace_status_code = 20 /* NamespaceStatusCode.Suspended */, modified_at_utc = @now, version = version + 1
        WHERE namespace_id = @namespace_id;
        SET @version = @version + 1;

        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id,
            entity_code, entity_id,
            event_message, details_format_id, details
        )
        VALUES (
            20 /* EventCode.NamespaceSuspended */, @now, @namespace_id,
            80 /* EntityCode.Namespace */, @namespace_id,
            @p_event_message, @p_details_format_id, @p_details
        );
    END;

    SELECT
        CAST(
            CASE
                WHEN @namespace_id IS NULL THEN 2 /* ControlAction.NotFound */
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
